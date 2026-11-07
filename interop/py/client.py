"""The probe client built on the Python SDK, from its published documentation.

usage: client.py (--stdio "CMD ARGS" | --url URL) [--mode auto|legacy] [--profile fixture|generic]
One JSON line per check on stdout.
"""
import argparse
import json
import shlex
import sys

import anyio

from mcp import Client, StdioServerParameters
from mcp.types import ElicitResult, ImageContent, TextContent, TextResourceContents

ap = argparse.ArgumentParser()
ap.add_argument("--stdio")
ap.add_argument("--url")
ap.add_argument("--mode", default="auto")
ap.add_argument("--profile", default="fixture")
args = ap.parse_args()


def emit(check, ok, detail=""):
    print(json.dumps({"check": check, "ok": ok, "detail": str(detail)[:300]}), flush=True)


async def step(check, fn):
    try:
        emit(check, True, await fn())
    except Exception as e:  # noqa: BLE001 - every failure is a result row
        emit(check, False, f"{type(e).__name__}: {e}")


def text(r):
    return "|".join(c.text for c in r.content if isinstance(c, TextContent))


async def elicit(context, params):
    return ElicitResult(action="accept", content={"confirm": True})


async def main():
    if args.stdio:
        parts = shlex.split(args.stdio)
        target = StdioServerParameters(command=parts[0], args=parts[1:])
    else:
        target = args.url
    try:
        async with Client(target, mode=args.mode, elicitation_callback=elicit) as c:
            emit("connect", True, f"server {c.server_info.name if c.server_info else None}; protocol {c.protocol_version}")
            await run(c)
    except Exception as e:  # noqa: BLE001
        emit("connect", False, f"{type(e).__name__}: {e}")
        sys.exit(1)


async def run(c):
    if args.profile == "generic":
        await step("tools/list", lambda: _names(c))
        async def rl():
            return f"{len((await c.list_resources()).resources)} resources"
        async def pl():
            return f"{len((await c.list_prompts()).prompts)} prompts"
        await step("resources/list", rl)
        await step("prompts/list", pl)
        return

    async def tl():
        n = await _names(c)
        if "echo" not in n:
            raise RuntimeError("no echo: " + n)
        return n
    await step("tools/list", tl)

    async def t_text():
        r = await c.call_tool("echo", {"message": "hi"})
        if text(r) != "hi":
            raise RuntimeError(repr(r))
        return text(r)
    await step("tools/call text", t_text)

    async def t_image():
        r = await c.call_tool("image", {})
        i = next((x for x in r.content if isinstance(x, ImageContent)), None)
        if i is None or i.mime_type != "image/png" or not i.data:
            raise RuntimeError(repr(r)[:200])
        return f"{i.mime_type} {len(i.data)} b64"
    await step("tools/call image", t_image)

    async def rlist():
        return ",".join(str(r.uri) for r in (await c.list_resources()).resources)
    await step("resources/list", rlist)

    async def rread():
        r = await c.read_resource("mem://hello")
        x = r.contents[0]
        if not isinstance(x, TextResourceContents):
            raise RuntimeError(repr(x))
        return x.text
    await step("resources/read", rread)

    async def plist():
        return ",".join(p.name for p in (await c.list_prompts()).prompts)
    await step("prompts/list", plist)

    async def pget():
        r = await c.get_prompt("greet", {"name": "Ann"})
        return r.messages[0].content.text
    await step("prompts/get", pget)

    async def unknown():
        try:
            r = await c.call_tool("nope", {})
        except Exception as e:  # noqa: BLE001
            return f"raised {type(e).__name__}: {e}"
        if r.is_error:
            return "isError result"
        raise RuntimeError("no error: " + repr(r)[:150])
    await step("error: unknown tool", unknown)

    async def progress():
        seen = []
        async def cb(p, total, message):
            seen.append(p)
        await c.call_tool("slow", {"steps": 3}, progress_callback=cb)
        await anyio.sleep(0.2)  # notifications arrive beside the response
        if not seen:
            raise RuntimeError("no progress notifications")
        return f"{len(seen)} notifications"
    await step("progress", progress)

    async def cancel():
        with anyio.move_on_after(0.4) as scope:
            await c.call_tool("wait", {})
        if not scope.cancelled_caught:
            raise RuntimeError("call completed")
        r = await c.call_tool("echo", {"message": "after"})
        return "cancelled; next call " + text(r)
    await step("cancellation", cancel)

    async def elic():
        r = await c.call_tool("confirm", {})
        if text(r) != "confirmed":
            raise RuntimeError(repr(r)[:200])
        return text(r)
    await step("elicitation", elic)


async def _names(c):
    return ",".join(t.name for t in (await c.list_tools()).tools)


anyio.run(main)
