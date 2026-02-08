FROM debian:bookworm-slim

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y \
    build-essential \
    ninja-build \
    gputils \
    git \
    python3 \
    nodejs \
    npm \
    curl \
    ca-certificates \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

ARG CMAKE_VERSION=4.2.3

RUN curl -L https://github.com/Kitware/CMake/releases/download/v${CMAKE_VERSION}/cmake-${CMAKE_VERSION}-linux-x86_64.sh -o cmake.sh \
    && chmod +x cmake.sh \
    && ./cmake.sh --prefix=/usr/local --skip-license \
    && rm cmake.sh

WORKDIR /app

CMD ["bash"]