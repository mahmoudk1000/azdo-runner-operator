FROM ubuntu:24.04

LABEL maintainer="Mahmoud Farouk aka mahmoudk1000 (mahmoudk1000@gmail.com)"

ARG VERSION=4.260.0
ARG RUNTIME_TYPE=base
ARG DOTNET_VERSION=8.0.404

ENV AZP_CAPABILITY=${RUNTIME_TYPE}

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    curl \
    git \
    jq \
    libicu74 \
    ca-certificates \
    gnupg \
    zip \
    unzip \
    apt-transport-https \
    software-properties-common \
    wget \
    dos2unix \
    openssh-client \
    python3-pip \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

RUN pip3 install --no-cache-dir --upgrade jinja2-cli pyyaml

RUN if [ "$RUNTIME_TYPE" = "java" ]; then \
    apt-get update && \
    apt-get install -y --no-install-recommends \
    openjdk-17-jre-headless \
    maven \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/* \
    && echo "java=true" > /tmp/capabilities.txt \
    && echo "maven=true" >> /tmp/capabilities.txt \
    && echo "jdk=true" >> /tmp/capabilities.txt; \
    elif [ "$RUNTIME_TYPE" = "dotnet" ]; then \
    curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh && \
    chmod +x dotnet-install.sh && \
    ./dotnet-install.sh --version ${DOTNET_VERSION} --install-dir /usr/share/dotnet && \
    ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet && \
    rm dotnet-install.sh && \
    echo "DOTNET_ROOT=/usr/share/dotnet" >> /etc/environment && \
    echo "PATH=/usr/share/dotnet:\$PATH" >> /etc/environment && \
    echo "dotnet=true" > /tmp/capabilities.txt \
    && echo ".net=true" >> /tmp/capabilities.txt \
    && echo "nuget=true" >> /tmp/capabilities.txt; \
    elif [ "$RUNTIME_TYPE" = "nodejs" ]; then \
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash - && \
    apt-get install -y --no-install-recommends \
    nodejs \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/* \
    && echo "node=true" > /tmp/capabilities.txt \
    && echo "nodejs=true" >> /tmp/capabilities.txt \
    && echo "npm=true" >> /tmp/capabilities.txt \
    && echo "javascript=true" >> /tmp/capabilities.txt; \
    else \
    echo "base=true" > /tmp/capabilities.txt; \
    fi

RUN useradd -m -s /bin/bash azureuser && \
    mkdir -p /azp && \
    chown azureuser:azureuser /azp

RUN curl -fsSL https://download.agent.dev.azure.com/agent/${VERSION}/vsts-agent-linux-x64-${VERSION}.tar.gz | \
    tar -xz -C /azp && \
    chown -R azureuser:azureuser /azp && \
    chmod +x /azp/*.sh /azp/bin/* /azp/externals/node/bin/* 2>/dev/null || true

RUN apt-get autoremove -y && \
    rm -rf /tmp/* /var/tmp/* /usr/share/doc/* /usr/share/man/* /var/cache/debconf/*

COPY start.sh /azp/start.sh
RUN dos2unix /azp/start.sh && \
    chmod +x /azp/start.sh && \
    chmod +x /azp/*.sh

RUN if [ -f /tmp/capabilities.txt ]; then \
    cp /tmp/capabilities.txt /azp/capabilities.txt && \
    chown azureuser:azureuser /azp/capabilities.txt; \
    fi

USER azureuser
WORKDIR /azp

ENV RUNTIME_TYPE=${RUNTIME_TYPE}

ENTRYPOINT ["/azp/start.sh"]
