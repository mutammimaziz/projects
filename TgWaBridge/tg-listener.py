from dotenv import load_dotenv
load_dotenv()
"""
tg-listener.py
Listens to Telegram alert groups and forwards each message to the mapped
WhatsApp group via the local wa-service.js HTTP API.
"""
import os
import asyncio
import logging

import httpx
from telethon import TelegramClient, events

logging.basicConfig(
    level=logging.INFO, format="%(asctime)s [tg] %(levelname)s %(message)s"
)
log = logging.getLogger("tg-listener")

API_ID = int(os.environ["TG_API_ID"])
API_HASH = os.environ["TG_API_HASH"]
WA_URL = os.environ.get("WA_SERVICE_URL", "http://127.0.0.1:3000")
TOKEN = os.environ.get("BRIDGE_TOKEN", "change-me")

# =====================================================================
# ROUTING TABLE - Telegram source group id -> WhatsApp destination
# Discover ids:
#   Telegram : python list-groups.py
#   WhatsApp : curl.exe -H "X-Bridge-Token: TOKEN" http://127.0.0.1:3000/groups
# =====================================================================
ROUTES = {
    -1001921125702: {"id": "120363033966812513@g.us"},  # AFM-WIN1 -> NCS JTC Server Team
    -1001938490464: {"id": "120363033966812513@g.us"},  # AFM-UNX1 -> NCS JTC Server Team
    -1001924561050: {"id": "120363046067303105@g.us"},  # AFM-DBA1 -> NCS Team at JTC
}

# ---------------------------------------------------------------------
# Resilient connection settings.
#   connection_retries=None -> retry forever instead of exiting after 5
#                              failures, which previously killed the
#                              process on a transient network drop.
#   retry_delay=5           -> wait 5s between attempts.
#   auto_reconnect=True     -> reconnect automatically after a drop.
# ---------------------------------------------------------------------
client = TelegramClient(
    "tg-bridge-session",
    API_ID,
    API_HASH,
    connection_retries=None,
    retry_delay=5,
    auto_reconnect=True,
    request_retries=5,
)
http = httpx.AsyncClient(timeout=30.0)


async def wait_for_wa_ready(retries=60, delay=2.0):
    """Block until the WhatsApp service reports ready."""
    for _ in range(retries):
        try:
            r = await http.get(WA_URL + "/health", headers={"X-Bridge-Token": TOKEN})
            if r.status_code == 200 and r.json().get("ready"):
                log.info("WA service is ready")
                return
        except Exception as e:
            log.debug("health check failed: %s", e)
        await asyncio.sleep(delay)
    log.warning("WA service not ready after wait; continuing anyway")


async def forward_to_wa(text, dest):
    payload = {"text": text, "groupId": dest["id"]}
    for attempt in range(1, 4):
        try:
            r = await http.post(
                WA_URL + "/send", json=payload, headers={"X-Bridge-Token": TOKEN}
            )
            if r.status_code == 200:
                log.info("forwarded -> %s", r.json().get("to"))
                return
            log.warning("send attempt %d got %s: %s", attempt, r.status_code, r.text)
        except Exception as e:
            log.warning("send attempt %d error: %s", attempt, e)
        await asyncio.sleep(2 * attempt)
    log.error("giving up on message after 3 attempts")


@client.on(events.NewMessage(chats=list(ROUTES.keys())))
async def route_handler(event):
    dest = ROUTES.get(event.chat_id)
    if not dest:
        return
    text = event.message.message
    if not text:
        return  # skip media-only messages with no caption
    log.info("msg from %s -> routing (%d chars)", event.chat_id, len(text))
    await forward_to_wa(text, dest)


async def main():
    # Outer guard: if anything escapes Telethon's own retry logic, log it
    # and start over rather than letting the process exit.
    while True:
        try:
            await client.start()
            log.info("Telegram connected. Waiting for WA service...")
            await wait_for_wa_ready()
            log.info("Routing %d Telegram group(s):", len(ROUTES))
            for src, dest in ROUTES.items():
                log.info("  %s  ->  %s", src, dest["id"])
            log.info("Bridge running.")
            await client.run_until_disconnected()
            log.warning("run_until_disconnected returned; reconnecting in 10s")
        except KeyboardInterrupt:
            log.info("interrupted by user; exiting")
            return
        except Exception as e:
            log.error("listener error (%s): %s - restarting loop in 10s",
                      type(e).__name__, e)
        await asyncio.sleep(10)


if __name__ == "__main__":
    asyncio.run(main())
