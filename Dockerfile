# Build-Kontext: der Ordner mit SyncDrive.sln.  docker build -t syncdrive-server .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/SyncDrive.Server.Host -c Release -a $TARGETARCH -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Daten (server.db, blocks/) liegen im Volume /data; der Dienst läuft ohne Root-Rechte.
USER root
RUN mkdir /data && chown app:app /data
USER app
VOLUME /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    SYNCDRIVE_Server__DataDirectory=/data \
    DOTNET_gcServer=0
EXPOSE 8080

# Das Image hat weder curl noch wget; bash genügt für eine HTTP-Anfrage an /healthz.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.0\r\n\r\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "SyncDrive.Server.Host.dll"]
