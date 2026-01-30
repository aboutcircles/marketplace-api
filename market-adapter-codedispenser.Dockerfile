FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy global props if they exist
COPY Directory.Build.props* ./

# Copy project file first for better layer caching
COPY Circles.Market.Adapters.CodeDispenser/Circles.Market.Adapters.CodeDispenser.csproj Circles.Market.Adapters.CodeDispenser/
COPY Circles.Market.Shared/Circles.Market.Shared.csproj Circles.Market.Shared/
RUN dotnet restore Circles.Market.Adapters.CodeDispenser/Circles.Market.Adapters.CodeDispenser.csproj

# Copy the rest and publish only the adapter project
COPY Circles.Market.Adapters.CodeDispenser/ Circles.Market.Adapters.CodeDispenser/
COPY Circles.Market.Shared/ Circles.Market.Shared/
RUN dotnet publish Circles.Market.Adapters.CodeDispenser/Circles.Market.Adapters.CodeDispenser.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
EXPOSE 5680
ENTRYPOINT ["dotnet", "Circles.Market.Adapters.CodeDispenser.dll"]
