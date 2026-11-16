# syntax=docker/dockerfile:1
# Optional one-shot bundle exporter; no listener and no artwork in this image.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY Directory.Build.props .
COPY src/Allegiance.Core/ src/Allegiance.Core/
COPY src/Allegiance.ClientCore.Browser/ src/Allegiance.ClientCore.Browser/
RUN dotnet publish src/Allegiance.ClientCore.Browser/Allegiance.ClientCore.Browser.csproj -c Release -o /publish
FROM alpine:3.22.2 AS bundle
COPY --from=build /publish/wwwroot /bundle
ENTRYPOINT ["sh", "-c", "cp -a /bundle/. /output/"]
