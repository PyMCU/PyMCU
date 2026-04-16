FROM gcc:bookworm

ARG TARGETARCH

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y \
    build-essential \
    ninja-build \
    gputils \
    binutils-avr \
    gcc-avr \
    avr-libc \
    git \
    python3 \
    nodejs \
    npm \
    curl \
    ca-certificates \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

ARG CMAKE_VERSION=4.2.3

RUN if [ "$TARGETARCH" = "arm64" ]; then \
        CMAKE_ARCH="aarch64"; \
    elif [ "$TARGETARCH" = "amd64" ]; then \
        CMAKE_ARCH="x86_64"; \
    else \
        echo "Not Supported Arch: $TARGETARCH" && exit 1; \
    fi \
    && curl -L "https://github.com/Kitware/CMake/releases/download/v${CMAKE_VERSION}/cmake-${CMAKE_VERSION}-linux-${CMAKE_ARCH}.sh" -o cmake.sh \
    && chmod +x cmake.sh \
    && ./cmake.sh --prefix=/usr/local --skip-license \
    && rm cmake.sh

WORKDIR /app

CMD ["bash"]