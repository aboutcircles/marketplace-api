FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy global props if they exist
COPY Directory.Build.props* ./

# Copy project file first for better layer caching
COPY Circles.Market.Adapters.CodeDispenser/Circles.Market.Adapters.CodeDispenser.csproj Circles.Market.Adapters.CodeDispenser/
COPY Circles.Market.Shared/Circles.Market.Shared.csproj Circles.Market.Shared/
COPY Circles.Market.Auth.Siwe/Circles.Market.Auth.Siwe.csproj Circles.Market.Auth.Siwe/
RUN dotnet restore Circles.Market.Adapters.CodeDispenser/Circles.Market.Adapters.CodeDispenser.csproj

# Copy the rest and publish only the adapter project
COPY Circles.Market.Adapters.CodeDispenser/ Circles.Market.Adapters.CodeDispenser/
COPY Circles.Market.Shared/ Circles.Market.Shared/
COPY Circles.Market.Auth.Siwe/ Circles.Market.Auth.Siwe/
RUN dotnet publish Circles.Market.Adapters.CodeDispenser/Circles.Market.Adapters.CodeDispenser.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

# Create non-root user with fixed UID 10000 (consistent across all circles services)
RUN addgroup -S -g 10000 circles && adduser -S -u 10000 -G circles circles

WORKDIR /app
COPY --from=build --chown=circles:circles /app/publish .

USER circles
EXPOSE 5680
ENTRYPOINT ["dotnet", "Circles.Market.Adapters.CodeDispenser.dll"]
