FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY IoTHunter.Shared/ IoTHunter.Shared/
COPY BackendProcessor/ BackendProcessor/

WORKDIR /src/BackendProcessor
RUN dotnet restore BackendProcessor.csproj
RUN dotnet publish BackendProcessor.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 80
EXPOSE 9464

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV DOTNET_EnableDiagnostics=0
ENV ASPNETCORE_URLS=http://+:80
ENV OpenTelemetry__OtlpEndpoint=http://otel-collector:4317
ENV Kafka__BootstrapServers=kafka:9092
ENV Postgres__ConnectionString=Host=postgres;Database=IoTHunter;Username=iotapp;Password=changeme
ENV Redis__ConnectionString=redis:6379

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BackendProcessor.dll"]
