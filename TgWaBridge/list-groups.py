from dotenv import load_dotenv
load_dotenv()
"""Prints every Telegram group/channel this account can see, with its chat_id.
Run during setup to fill in the ROUTES table in tg-listener.py."""
import os
from telethon import TelegramClient

client = TelegramClient(
    "tg-bridge-session", int(os.environ["TG_API_ID"]), os.environ["TG_API_HASH"]
)


async def main():
    await client.start()
    print("")
    print("chat_id           title")
    print("----------------  -----")
    async for d in client.iter_dialogs():
        if d.is_group or d.is_channel:
            print(str(d.id).ljust(16), " ", d.name)
    print("")


with client:
    client.loop.run_until_complete(main())
