# Use slim base image for smaller size
FROM ubuntu:24.04

LABEL maintainer="Mahmoud Farouk aka mahmoudk1000 (mahmoudk1000@gmail.com)"

ARG VERSION=4.260.0
ARG RUNTIME_TYPE=base

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    curl \
    git \
    jq \
    libicu74 \
    ca-certificates \
    gnupg \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

RUN if [ "$RUNTIME_TYPE" = "java" ]; then \
    apt-get update && \
    apt-get install -y --no-install-recommends \
    openjdk-17-jre-headless \
    maven \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*; \
    elif [ "$RUNTIME_TYPE" = "dotnet" ]; then \
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg && \
    echo "deb [arch=amd64,arm64,armhf signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/repos/microsoft-ubuntu-jammy-prod jammy main" > /etc/apt/sources.list.d/microsoft-prod.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
    dotnet-runtime-8.0 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*; \
    elif [ "$RUNTIME_TYPE" = "nodejs" ]; then \
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash - && \
    apt-get install -y --no-install-recommends \
    nodejs \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*; \
    fi

RUN useradd -m -s /bin/bash azureuser && \
    mkdir -p /azp && \
    chown azureuser:azureuser /azp

RUN curl -fsSL https://download.agent.dev.azure.com/agent/${VERSION}/vsts-agent-linux-x64-${VERSION}.tar.gz | \
    tar -xz -C /azp && \
    chown -R azureuser:azureuser /azp

RUN apt-get autoremove -y && \
    rm -rf /tmp/* /var/tmp/* /usr/share/doc/* /usr/share/man/* /var/cache/debconf/*

COPY start.sh /azp/start.sh
RUN chmod +x /azp/start.sh

USER azureuser
WORKDIR /azp

ENTRYPOINT ["/azp/start.sh"]