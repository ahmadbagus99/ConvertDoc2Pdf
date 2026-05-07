FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DocToPdfService/DocToPdfService.csproj ./DocToPdfService/
RUN dotnet restore ./DocToPdfService/DocToPdfService.csproj

COPY DocToPdfService/ ./DocToPdfService/
RUN dotnet publish ./DocToPdfService/DocToPdfService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install LibreOffice and fonts
RUN apt-get update && apt-get install -y --no-install-recommends \
    libreoffice \
    libreoffice-writer \
    fonts-liberation \
    fonts-dejavu \
    fontconfig \
    && fc-cache -fv \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN useradd -m -u 1001 appuser
USER appuser

COPY --from=build --chown=appuser:appuser /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV LibreOffice__ExecutablePath=/usr/bin/libreoffice

EXPOSE 8080

ENTRYPOINT ["dotnet", "DocToPdfService.dll"]
