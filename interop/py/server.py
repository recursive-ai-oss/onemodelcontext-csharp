"""The fixture server on the Python SDK, from its published documentation.

usage: server.py stdio | http PORT
"""
import base64
import sys
from typing import Annotated

import anyio
from pydantic import BaseModel

from mcp.server import MCPServer
from mcp.server.mcpserver import AcceptedElicitation, Context, Elicit, ElicitationResult, Image, Resolve

PNG_1X1 = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==")

mcp = MCPServer("interop-py-server")


class Confirm(BaseModel):
    confirm: bool


@mcp.tool()
def echo(message: str) -> str:
    """Echoes the message."""
    return message


@mcp.tool()
def image() -> Image:
    """A one-pixel PNG."""
    return Image(data=PNG_1X1, format="png")


@mcp.tool()
async def slow(steps: int, ctx: Context) -> str:
    """Reports progress."""
    for i in range(1, steps + 1):
        await ctx.report_progress(i, total=steps)
        await anyio.sleep(0.05)
    return "done"


@mcp.tool()
async def wait() -> str:
    """Waits until cancelled."""
    await anyio.sleep(15)
    return "timed out"


async def ask() -> Elicit[Confirm]:
    """Resolver: the question is asked on any protocol era (a request on legacy, an input_required round on 2026-07-28)."""
    return Elicit("Proceed?", Confirm)


@mcp.tool()
async def confirm(answer: Annotated[ElicitationResult[Confirm], Resolve(ask)]) -> str:
    """Asks the user to confirm."""
    return "confirmed" if isinstance(answer, AcceptedElicitation) and answer.data.confirm else "declined"


@mcp.resource("mem://hello", mime_type="text/plain")
def hello() -> str:
    """A greeting."""
    return "hello from interop"


@mcp.prompt()
def greet(name: str) -> str:
    """Greets someone."""
    return f"Say hello to {name}."


if __name__ == "__main__":
    if sys.argv[1] == "stdio":
        mcp.run()
    else:
        mcp.run(transport="streamable-http", host="127.0.0.1", port=int(sys.argv[2]))
