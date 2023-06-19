# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:6.0.404-alpine3.16 AS builder
WORKDIR /src
COPY src /src/
RUN dotnet publish -c Release /src/AvisoConsumo.csproj -o /app

FROM mcr.microsoft.com/dotnet/aspnet:6.0.12-alpine3.16
VOLUME ["/data"]
COPY --from=builder /app /app
ENTRYPOINT [ "/app/AvisoConsumo" ]
