FROM ubuntu:22.04

LABEL maintainer="Mahmoud Farouk aka mahmoudk1000 (mahmoudk1000@gmail.com)"

ARG VERSION=4.259.0
ARG RUNTIME_TYPE=base
ARG DOTNET_VERSION=8.0.404
ARG YQ_VERSION=4.44.2
ARG KUBECTL_VERSION=1.31.2
ARG HELM_VERSION=3.16.2
ARG NODEJS_VERSION=20

ENV AZP_CAPABILITY=${RUNTIME_TYPE}

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    apt-transport-https \
    ca-certificates \
    curl \
    dos2unix \
    git \
    gnupg \
    jq \
    libicu70 \
    openssh-client \
    python3 \
    python3-pip \
    software-properties-common \
    unzip \
    wget \
    zip \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

RUN pip3 install --no-cache-dir --upgrade jinja2-cli pyyaml

RUN curl -sSL https://github.com/mikefarah/yq/releases/download/v${YQ_VERSION}/yq_linux_amd64 -o /usr/local/bin/yq \
    && chmod +x /usr/local/bin/yq \
    && curl -sSL https://dl.k8s.io/release/v${KUBECTL_VERSION}/bin/linux/amd64/kubectl -o /usr/local/bin/kubectl \
    && chmod +x /usr/local/bin/kubectl \
    && curl -sSL https://get.helm.sh/helm-v${HELM_VERSION}-linux-amd64.tar.gz -o helm.tar.gz \
    && tar -zxf helm.tar.gz \
    && mv linux-amd64/helm /usr/local/bin/helm \
    && chmod +x /usr/local/bin/helm \
    && rm -rf helm.tar.gz linux-amd64

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
    curl -fsSL https://deb.nodesource.com/setup_${NODEJS_VERSION}.x | bash - && \
    apt-get install -y --no-install-recommends \
    nodejs \
    chromium-browser \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/* \
    && echo "node=true" > /tmp/capabilities.txt \
    && echo "nodejs=true" >> /tmp/capabilities.txt \
    && echo "npm=true" >> /tmp/capabilities.txt \
    && echo "javascript=true" >> /tmp/capabilities.txt \
    && export CHROME_BIN=$(which chromium); \
    else \
    echo "base=true" > /tmp/capabilities.txt; \
    fi

RUN groupadd -g 1001 agent && \
    useradd -m -u 1001 -g 1001 -s /bin/bash agent && \
    mkdir -p /azp && \
    chown agent:agent /azp

RUN curl -fsSL https://download.agent.dev.azure.com/agent/${VERSION}/vsts-agent-linux-x64-${VERSION}.tar.gz | \
    tar -xz -C /azp && \
    chown -R agent:agent /azp && \
    chmod +x /azp/*.sh /azp/bin/* /azp/externals/node/bin/* 2>/dev/null || true

RUN apt-get autoremove -y && \
    rm -rf /tmp/* /var/tmp/* /usr/share/doc/* /usr/share/man/* /var/cache/debconf/*

COPY start.sh /azp/start.sh
RUN dos2unix /azp/start.sh && \
    chmod +x /azp/start.sh && \
    chmod +x /azp/*.sh

RUN if [ -f /tmp/capabilities.txt ]; then \
    cp /tmp/capabilities.txt /azp/capabilities.txt && \
    chown agent:agent /azp/capabilities.txt; \
    fi

USER agent
WORKDIR /azp

ENV RUNTIME_TYPE=${RUNTIME_TYPE}

ENTRYPOINT ["/azp/start.sh"]

