FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props* ./
COPY NuGet.Config ./
COPY nuget-local/ nuget-local/

COPY Circles.Market.Adapters.Unlock/Circles.Market.Adapters.Unlock.csproj Circles.Market.Adapters.Unlock/
COPY Circles.Market.Shared/Circles.Market.Shared.csproj Circles.Market.Shared/
COPY Circles.Market.Auth.Siwe/Circles.Market.Auth.Siwe.csproj Circles.Market.Auth.Siwe/
COPY Circles.Market.Fulfillment.Core/Circles.Market.Fulfillment.Core.csproj Circles.Market.Fulfillment.Core/
RUN dotnet restore Circles.Market.Adapters.Unlock/Circles.Market.Adapters.Unlock.csproj

COPY Circles.Market.Adapters.Unlock/ Circles.Market.Adapters.Unlock/
COPY Circles.Market.Shared/ Circles.Market.Shared/
COPY Circles.Market.Auth.Siwe/ Circles.Market.Auth.Siwe/
COPY Circles.Market.Fulfillment.Core/ Circles.Market.Fulfillment.Core/
RUN dotnet publish Circles.Market.Adapters.Unlock/Circles.Market.Adapters.Unlock.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
RUN addgroup -S -g 10000 circles && adduser -S -u 10000 -G circles circles

WORKDIR /app
COPY --from=build --chown=circles:circles /app/publish .

USER circles
EXPOSE 5682
ENTRYPOINT ["dotnet", "Circles.Market.Adapters.Unlock.dll"]
