import asyncio
import os
from contextlib import asynccontextmanager

from fastapi import FastAPI
from pydantic import BaseModel
from playwright.async_api import Error as PlaywrightError
from playwright.async_api import TimeoutError as PlaywrightTimeoutError
from playwright.async_api import async_playwright


class RenderHtmlRequest(BaseModel):
    url: str


class RenderHtmlResponse(BaseModel):
    isSuccess: bool
    html: str | None = None
    error: str | None = None
    finalUrl: str | None = None
    statusCode: int | None = None


PLAYWRIGHT_ARGS = ["--no-sandbox", "--disable-dev-shm-usage"]


@asynccontextmanager
async def lifespan(_: FastAPI):
    app.state.playwright = await async_playwright().start()
    app.state.browser = await app.state.playwright.chromium.launch(
        headless=True,
        args=PLAYWRIGHT_ARGS,
    )
    try:
        yield
    finally:
        await app.state.browser.close()
        await app.state.playwright.stop()


app = FastAPI(lifespan=lifespan)


async def get_stable_page_content(page) -> str | None:
    max_attempts = 6
    retry_delay_seconds = 0.25

    for attempt in range(max_attempts):
        try:
            await page.wait_for_load_state("domcontentloaded", timeout=5000)
            try:
                await page.wait_for_load_state("networkidle", timeout=3000)
            except PlaywrightTimeoutError:
                pass

            return await page.content()
        except PlaywrightError as ex:
            message = str(ex).lower()
            if "page is navigating" not in message and "changing the content" not in message:
                raise

            if attempt == max_attempts - 1:
                return None

            await asyncio.sleep(retry_delay_seconds)

    return None


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/browser/render-html", response_model=RenderHtmlResponse)
async def render_html(request: RenderHtmlRequest) -> RenderHtmlResponse:
    context = await app.state.browser.new_context()
    page = await context.new_page()

    try:
        response = await page.goto(
            request.url,
            timeout=60000,
            wait_until="domcontentloaded",
        )

        if response is None:
            return RenderHtmlResponse(
                isSuccess=False,
                error="Navigation returned no response",
                finalUrl=page.url,
            )

        if response.status >= 400:
            return RenderHtmlResponse(
                isSuccess=False,
                error=f"HTTP {response.status}",
                finalUrl=page.url,
                statusCode=response.status,
            )

        html = await get_stable_page_content(page)
        if not html or not html.strip():
            return RenderHtmlResponse(
                isSuccess=False,
                error="Page returned empty HTML",
                finalUrl=page.url,
                statusCode=response.status,
            )

        return RenderHtmlResponse(
            isSuccess=True,
            html=html,
            finalUrl=page.url,
            statusCode=response.status,
        )
    except PlaywrightTimeoutError:
        return RenderHtmlResponse(isSuccess=False, error="Navigation timed out")
    except PlaywrightError as ex:
        return RenderHtmlResponse(isSuccess=False, error=str(ex))
    except Exception as ex:
        return RenderHtmlResponse(isSuccess=False, error=str(ex))
    finally:
        await context.close()


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        "browser_api:app",
        host="0.0.0.0",
        port=int(os.getenv("BROWSER_RENDER_PORT", "3001")),
    )
