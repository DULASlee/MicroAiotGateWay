FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY IoTHunter.Shared/ IoTHunter.Shared/
COPY DeviceSimulator/ DeviceSimulator/

WORKDIR /src/DeviceSimulator
RUN dotnet restore DeviceSimulator.csproj
RUN dotnet publish DeviceSimulator.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 80
EXPOSE 9464

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV DOTNET_EnableDiagnostics=0
ENV ASPNETCORE_URLS=http://+:80;http://+:9464
ENV OpenTelemetry__OtlpEndpoint=http://otel-collector:4317

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DeviceSimulator.dll"]
