FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish DeviceSimulator/DeviceSimulator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5091
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 5091
ENTRYPOINT ["dotnet", "DeviceSimulator.dll"]
