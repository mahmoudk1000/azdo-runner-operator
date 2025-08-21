FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build

WORKDIR /src

COPY ["AzDORunner.csproj", "./"]

RUN dotnet restore "AzDORunner.csproj" \
    --runtime linux-musl-x64

COPY . .

RUN dotnet publish "AzDORunner.csproj" \
    -c Release \
    -o /app/publish \
    --runtime linux-musl-x64 \
    --self-contained false \
    --no-restore \
    /p:UseAppHost=false \
    /p:PublishTrimmed=false \
    /p:PublishSingleFile=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime

LABEL maintainer="Mahmoud Farouk aka mahmoudk1000 (mahmoudk1000@gmail.com)"

RUN apk add --no-cache \
    icu-libs \
    tzdata \
    && addgroup -g 1001 -S azdo-operator \
    && adduser -u 1001 -S azdo-operator -G azdo-operator

WORKDIR /app

COPY --from=build --chown=azdo-operator:azdo-operator /app/publish .

USER azdo-operator

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "AzDORunner.dll"]