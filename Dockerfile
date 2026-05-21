# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage - Ubuntu 22.04 (má správne verzie knižníc pre OpenCV)
FROM ubuntu:22.04 AS runtime
WORKDIR /app

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y wget ca-certificates \
    && wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y aspnetcore-runtime-9.0 \
    && rm -rf /var/lib/apt/lists/*

RUN apt-get update && apt-get install -y \
    libgdiplus \
    libx11-6 \
    libxext6 \
    libglib2.0-0 \
    libgomp1 \
    libjpeg-turbo8 \
    libavcodec58 \
    libavformat58 \
    libavutil56 \
    libswscale5 \
    libtesseract4 \
    libgtk2.0-0 \
    libopenexr25 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

EXPOSE 5250

ENTRYPOINT ["dotnet", "GUIVideoProcessing.Web.dll"]
