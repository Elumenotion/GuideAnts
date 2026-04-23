FROM mcr.microsoft.com/mssql/server:2025-RTM-ubuntu-22.04

USER root
RUN apt-get update \
    && apt-get install -y curl gnupg \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && curl -fsSL https://packages.microsoft.com/config/ubuntu/22.04/mssql-server-2025.list | tee /etc/apt/sources.list.d/mssql-server-2025.list > /dev/null \
    && apt-get update \
    && apt-get install -y mssql-server-fts \
    && rm -rf /var/lib/apt/lists/*
USER mssql

ARG MSSQL_PID=Developer
ENV MSSQL_PID=${MSSQL_PID}
