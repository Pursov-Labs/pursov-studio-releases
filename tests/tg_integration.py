"""Explicit live test: starts only an owned proxy on a free loopback port."""
import asyncio
import json
import os
from pathlib import Path
import socket
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'runtime'))
from tg_probe import probe


async def main():
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1', 0))
        port = reservation.getsockname()[1]
    config = {'host': '127.0.0.1', 'port': port, 'secret': os.urandom(16).hex(),
              'dc_ip': ['2:149.154.167.220', '4:149.154.167.220'], 'pool_size': 0}
    process = await asyncio.create_subprocess_exec(
        str(ROOT / 'runtime/python/python.exe'), '-B', '-u', str(ROOT / 'runtime/tg_runner.py'),
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
        creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        process.stdin.write((json.dumps({'source': str(ROOT / 'payload/tg/1.11.4'), 'config': config}) + '\n').encode())
        await process.stdin.drain()
        event = json.loads(await asyncio.wait_for(process.stdout.readline(), 25))
        assert event['event'] == 'ready', event
        result = await probe(config)
        print(json.dumps(result, ensure_ascii=True))
        assert result['success'], 'No verified MTProto round trip; network-dependent failure'
        process.stdin.write(b'{"command":"stop"}\n')
        await process.stdin.drain()
        process.stdin.close()
        assert await asyncio.wait_for(process.wait(), 8) == 0
        print('PASS: owned runner start, real MTProto roundtrip, graceful stop')
    finally:
        if process.returncode is None:
            process.kill()
            await process.wait()


if __name__ == '__main__':
    asyncio.run(main())
