FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Momos.Host/Momos.Host.csproj src/Momos.Host/
RUN dotnet restore src/Momos.Host/Momos.Host.csproj

COPY src/Momos.Host/ src/Momos.Host/
RUN dotnet publish src/Momos.Host/Momos.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

RUN mkdir -p /data
ENV ConnectionStrings__MomosDb="Data Source=/data/momos.db"
ENV Momos__Host__Knowledge__SqlitePath="/data/knowledge.db"

ENTRYPOINT ["dotnet", "Momos.Host.dll"]
