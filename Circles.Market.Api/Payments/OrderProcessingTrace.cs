using System.Text.Json;
using System.Threading.Channels;
using Npgsql;

namespace Circles.Market.Api.Payments;

public sealed record OrderProcessingTraceEvent(
    DateTimeOffset OccurredAt,
    string Stage,
    string Status,
    string? OrderId,
    string? PaymentReference,
    long? ChainId,
    string? SellerAddress,
    string? BuyerAddress,
    string? ReasonCode,
    string? Message,
    JsonElement? Details);

public interface IOrderProcessingTraceSink
{
    void Emit(OrderProcessingTraceEvent evt);
}

public sealed class NoopOrderProcessingTraceSink : IOrderProcessingTraceSink
{
    public void Emit(OrderProcessingTraceEvent evt)
    {
    }
}

public sealed class AsyncBufferedOrderProcessingTraceSink : BackgroundService, IOrderProcessingTraceSink
{
    private readonly ILogger<AsyncBufferedOrderProcessingTraceSink> _log;
    private readonly string? _connString;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _dbTimeout;
    private readonly Channel<OrderProcessingTraceEvent> _channel;

    private volatile bool _dbWritable;

    public AsyncBufferedOrderProcessingTraceSink(ILogger<AsyncBufferedOrderProcessingTraceSink> log)
    {
        _log = log;

        _connString = Environment.GetEnvironmentVariable("ORDER_TRACE_POSTGRES_CONNECTION");
        _batchSize = Math.Clamp(
            int.TryParse(Environment.GetEnvironmentVariable("ORDER_TRACE_BATCH_SIZE"), out var bs) ? bs : 100,
            1,
            1000);
        _flushInterval = TimeSpan.FromMilliseconds(Math.Clamp(
            int.TryParse(Environment.GetEnvironmentVariable("ORDER_TRACE_FLUSH_INTERVAL_MS"), out var fm) ? fm : 1000,
            100,
            10_000));
        _dbTimeout = TimeSpan.FromMilliseconds(Math.Clamp(
            int.TryParse(Environment.GetEnvironmentVariable("ORDER_TRACE_DB_TIMEOUT_MS"), out var tm) ? tm : 500,
            100,
            10_000));

        var queueCapacity = Math.Clamp(
            int.TryParse(Environment.GetEnvironmentVariable("ORDER_TRACE_QUEUE_CAPACITY"), out var qc) ? qc : 5000,
            100,
            100_000);

        _channel = Channel.CreateBounded<OrderProcessingTraceEvent>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _dbWritable = await EnsureSchemaAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _dbWritable = false;
            _log.LogWarning(ex, "Order trace sink initialization failed; tracing will be disabled (fail-open)");
        }

        await base.StartAsync(cancellationToken);
    }

    public void Emit(OrderProcessingTraceEvent evt)
    {
        if (!_dbWritable)
        {
            return;
        }

        _channel.Writer.TryWrite(evt);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<OrderProcessingTraceEvent>(_batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (buffer.Count < _batchSize && _channel.Reader.TryRead(out var item))
                {
                    buffer.Add(item);
                }

                if (buffer.Count == 0)
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    waitCts.CancelAfter(_flushInterval);
                    try
                    {
                        await _channel.Reader.WaitToReadAsync(waitCts.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Timed flush interval elapsed with no new items.
                    }

                    continue;
                }

                bool flushBySize = buffer.Count >= _batchSize;
                if (!flushBySize)
                {
                    // Small batch: wait up to flush interval for more items, then flush whatever we have.
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    waitCts.CancelAfter(_flushInterval);
                    try
                    {
                        await _channel.Reader.WaitToReadAsync(waitCts.Token);
                        while (buffer.Count < _batchSize && _channel.Reader.TryRead(out var item2))
                        {
                            buffer.Add(item2);
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Interval elapsed; flush current buffer.
                    }
                }

                await FlushBatchAsync(buffer, stoppingToken);
                buffer.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Order trace sink flush failed; dropping current trace batch (fail-open)");
                buffer.Clear();
            }
        }
    }

    private async Task<bool> EnsureSchemaAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connString))
        {
            _log.LogWarning("ORDER_TRACE_POSTGRES_CONNECTION missing; tracing disabled");
            return false;
        }

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS order_processing_events (
  id                bigserial PRIMARY KEY,
  occurred_at       timestamptz NOT NULL,
  order_id          text NULL,
  payment_reference text NULL,
  chain_id          bigint NULL,
  seller_address    text NULL,
  buyer_address     text NULL,
  stage             text NOT NULL,
  status            text NOT NULL,
  reason_code       text NULL,
  message           text NULL,
  details_json      jsonb NULL
);

CREATE INDEX IF NOT EXISTS ix_order_processing_events_order
  ON order_processing_events (order_id, occurred_at);

CREATE INDEX IF NOT EXISTS ix_order_processing_events_payment
  ON order_processing_events (payment_reference, occurred_at);

CREATE INDEX IF NOT EXISTS ix_order_processing_events_stage
  ON order_processing_events (stage, occurred_at);
";

        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation("Order trace sink enabled");

        _channel.Writer.TryWrite(new OrderProcessingTraceEvent(
            OccurredAt: DateTimeOffset.UtcNow,
            Stage: "trace_sink_started",
            Status: "info",
            OrderId: null,
            PaymentReference: null,
            ChainId: null,
            SellerAddress: null,
            BuyerAddress: null,
            ReasonCode: null,
            Message: "Order trace sink initialized",
            Details: null));

        return true;
    }

    private async Task FlushBatchAsync(List<OrderProcessingTraceEvent> batch, CancellationToken ct)
    {
        if (!_dbWritable || batch.Count == 0 || string.IsNullOrWhiteSpace(_connString))
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_dbTimeout);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(timeout.Token);
        await using var tx = await conn.BeginTransactionAsync(timeout.Token);

        foreach (var evt in batch)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO order_processing_events
(occurred_at, order_id, payment_reference, chain_id, seller_address, buyer_address, stage, status, reason_code, message, details_json)
VALUES
(@t, @oid, @pref, @chain, @seller, @buyer, @stage, @status, @reason, @msg, @details);";

            cmd.Parameters.AddWithValue("@t", evt.OccurredAt);
            cmd.Parameters.AddWithValue("@oid", (object?)evt.OrderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pref", (object?)evt.PaymentReference ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@chain", (object?)evt.ChainId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@seller", (object?)evt.SellerAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@buyer", (object?)evt.BuyerAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@stage", evt.Stage);
            cmd.Parameters.AddWithValue("@status", evt.Status);
            cmd.Parameters.AddWithValue("@reason", (object?)evt.ReasonCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@msg", (object?)evt.Message ?? DBNull.Value);

            var detailsJson = evt.Details.HasValue
                ? JsonSerializer.Serialize(evt.Details.Value)
                : null;

            cmd.Parameters.AddWithValue("@details", (object?)detailsJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(timeout.Token);
        }

        await tx.CommitAsync(timeout.Token);
    }
}
