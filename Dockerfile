FROM debian:bookworm-slim

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y \
    build-essential \
    cmake \
    ninja-build \
    gputils \
    git \
    python3 \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /app

CMD ["bash"]