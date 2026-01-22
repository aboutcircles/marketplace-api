FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy global props if they exist
COPY Directory.Build.props* ./

# Copy project files first for better layer caching
COPY Circles.Market.Api/Circles.Market.Api.csproj Circles.Market.Api/
COPY Circles.Market.Shared/Circles.Market.Shared.csproj Circles.Market.Shared/

RUN dotnet restore Circles.Market.Api/Circles.Market.Api.csproj

# Copy the rest of the source and publish only the Market API project
COPY Circles.Market.Api/ Circles.Market.Api/
COPY Circles.Market.Shared/ Circles.Market.Shared/

RUN dotnet publish Circles.Market.Api/Circles.Market.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
EXPOSE 5084
ENTRYPOINT ["dotnet", "Circles.Market.Api.dll"]
