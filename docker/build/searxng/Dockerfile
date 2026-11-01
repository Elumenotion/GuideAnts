FROM docker.io/searxng/searxng:2026.10.30-ffa88e3ba AS searxng-source

FROM mcr.microsoft.com/playwright/python:v1.64.1-noble

WORKDIR /usr/local/searxng

RUN groupadd -r searxng && \
    useradd -r -g searxng -d /usr/local/searxng -s /bin/sh searxng && \
    mkdir -p /etc/searxng /var/cache/searxng /opt/browser-render

RUN apt-get update && \
    apt-get install -y --no-install-recommends nginx tini && \
    rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir \
        annotated-doc==0.0.5 \
        annotated-types==0.8.0 \
        anyio==4.16.0 \
        babel==2.18.0 \
        blinker==1.9.0 \
        certifi==2026.10.23 \
        cffi==2.1.1 \
        click==8.5.0 \
        curl_cffi==0.17.0 \
        fastapi==0.136.3 \
        flask-babel==4.0.0 \
        flask==3.1.3 \
        granian==2.8.5 \
        greenlet==3.5.6 \
        h11==0.16.0 \
        idna==3.20 \
        isodate==0.7.2 \
        itsdangerous==2.2.0 \
        Jinja2==3.1.6 \
        lxml==6.1.3 \
        markdown-it-py==4.2.0 \
        MarkupSafe==3.0.4 \
        mdurl==0.1.2 \
        msgspec==0.22.0 \
        playwright==1.64.1 \
        pycparser==3.12 \
        pydantic-core==2.50.2 \
        pydantic==2.14.2 \
        pyee==13.0.1 \
        Pygments==2.21.0 \
        python-dateutil==3.0.0 \
        pytz==2026.6 \
        PyYAML==6.0.3 \
        rich==15.0.0 \
        setproctitle==1.3.8 \
        shellingham==1.5.4 \
        six==1.17.0 \
        starlette==1.2.1 \
        typer==0.27.3 \
        typing-inspection==0.4.4 \
        typing_extensions==4.16.0 \
        tzdata==2026.6 \
        uvicorn==0.34.2 \
        valkey==6.1.1 \
        Werkzeug==3.1.9 \
        whitenoise==6.12.0 \
        granian[pname]==2.8.5 && \
    python -m playwright install --with-deps chromium

COPY --from=searxng-source /usr/local/searxng/searx /usr/local/searxng/searx
COPY --from=searxng-source /usr/local/searxng/entrypoint.sh /usr/local/searxng/entrypoint.sh
COPY docker/build/searxng/browser_api.py /opt/browser-render/browser_api.py
COPY docker/build/searxng/entrypoint-with-browser.sh /usr/local/bin/entrypoint-with-browser.sh
COPY docker/build/searxng/nginx.conf /etc/nginx/nginx.conf

RUN mkdir -p /usr/local/searxng/.venv/bin && \
    ln -sf /usr/local/bin/granian /usr/local/searxng/.venv/bin/granian && \
    chmod +x /usr/local/searxng/entrypoint.sh /usr/local/bin/entrypoint-with-browser.sh && \
    chown -R searxng:searxng /usr/local/searxng /etc/searxng /var/cache/searxng /opt/browser-render

ENV __SEARXNG_CONFIG_PATH=/etc/searxng \
    CONFIG_PATH=/etc/searxng \
    __SEARXNG_DATA_PATH=/var/cache/searxng \
    DATA_PATH=/var/cache/searxng \
    __SEARXNG_SETTINGS_PATH=/etc/searxng/settings.yml \
    __SEARXNG_VERSION=custom-playwright \
    GRANIAN_PROCESS_NAME=searxng \
    GRANIAN_INTERFACE=wsgi \
    GRANIAN_HOST=127.0.0.1 \
    GRANIAN_PORT=8081 \
    GRANIAN_WEBSOCKETS=false \
    GRANIAN_BLOCKING_THREADS=4 \
    GRANIAN_WORKERS_KILL_TIMEOUT=30s \
    GRANIAN_BLOCKING_THREADS_IDLE_TIMEOUT=5m \
    BROWSER_RENDER_PORT=3001

EXPOSE 8080

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint-with-browser.sh"]
