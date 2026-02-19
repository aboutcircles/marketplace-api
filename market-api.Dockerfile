FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy global props if they exist
COPY Directory.Build.props* ./

# Copy project files first for better layer caching
COPY Circles.Market.Api/Circles.Market.Api.csproj Circles.Market.Api/
COPY Circles.Market.Shared/Circles.Market.Shared.csproj Circles.Market.Shared/
COPY Circles.Market.Auth.Siwe/Circles.Market.Auth.Siwe.csproj Circles.Market.Auth.Siwe/
COPY Circles.Market.Fulfillment.Core/Circles.Market.Fulfillment.Core.csproj Circles.Market.Fulfillment.Core/

RUN dotnet restore Circles.Market.Api/Circles.Market.Api.csproj

# Copy the rest of the source and publish only the Market API project
COPY Circles.Market.Api/ Circles.Market.Api/
COPY Circles.Market.Shared/ Circles.Market.Shared/
COPY Circles.Market.Auth.Siwe/ Circles.Market.Auth.Siwe/
COPY Circles.Market.Fulfillment.Core/ Circles.Market.Fulfillment.Core/

RUN dotnet publish Circles.Market.Api/Circles.Market.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

# Create non-root user with fixed UID 10000 (consistent across all circles services)
RUN addgroup -S -g 10000 circles && adduser -S -u 10000 -G circles circles

WORKDIR /app
COPY --from=build --chown=circles:circles /app/publish .

USER circles
EXPOSE 5084
ENTRYPOINT ["dotnet", "Circles.Market.Api.dll"]
