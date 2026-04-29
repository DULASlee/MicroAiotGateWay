FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY IoTHunter.Shared/ IoTHunter.Shared/
COPY IoTGateway/ IoTGateway/

WORKDIR /src/IoTGateway
RUN dotnet restore IoTGateway.csproj
RUN dotnet publish IoTGateway.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 80
EXPOSE 9464

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV DOTNET_EnableDiagnostics=0
ENV ASPNETCORE_URLS=http://+:80
ENV OpenTelemetry__OtlpEndpoint=http://otel-collector:4317

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "IoTGateway.dll"]

# TLS 预留：将证书拷贝至 /app/certs，环境变量 ASPNETCORE_Kestrel__Certificates__Default__Path=/app/certs/tls.crt
# RUN mkdir -p /app/certs && COPY certs/* /app/certs/
