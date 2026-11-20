"""Quaykeep's TCP/UDP relay. Both endpoints are numeric and belong to different families."""
import asyncio
import ipaddress
import os
import selectors
import socket
import sys
import time
from collections import OrderedDict

TCP_LIMIT = 256
UDP_LIMIT = 512
UDP_IDLE = 60
# Linux exposes IP_PKTINFO even on Python builds that do not export the constant.
IP_PKTINFO = getattr(socket, "IP_PKTINFO", 8)


def endpoint(address, port):
    if ipaddress.ip_address(address).version == 6:
        return socket.AF_INET6, (address, port, 0, 0)
    return socket.AF_INET, (address, port)


def listen(family, address, kind):
    sock = socket.socket(family, kind)
    try:
        if family == socket.AF_INET6:
            sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
        if kind == socket.SOCK_STREAM:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind(address)
        sock.setblocking(False)
        return sock
    except BaseException:
        sock.close()
        raise


def ready():
    address = os.environ.get("NOTIFY_SOCKET")
    if address:
        if address.startswith("@"):
            address = "\0" + address[1:]
        with socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM) as notifier:
            notifier.sendto(b"READY=1", address)


async def copy(reader, writer):
    while True:
        data = await reader.read(65536)
        if not data:
            writer.write_eof()
            return
        writer.write(data)
        await writer.drain()


async def tcp(local_family, local, remote_family, remote):
    active = 0

    async def relay(reader, writer):
        nonlocal active
        if active >= TCP_LIMIT:
            writer.close()
            return
        active += 1
        upstream = None
        pumps = []
        try:
            upstream_reader, upstream = await asyncio.wait_for(
                asyncio.open_connection(remote[0], remote[1], family=remote_family,
                                        flags=socket.AI_NUMERICHOST), 5)
            pumps = [asyncio.create_task(copy(reader, upstream)),
                     asyncio.create_task(copy(upstream_reader, writer))]
            await asyncio.gather(*pumps)
        except (OSError, asyncio.TimeoutError):
            pass
        finally:
            active -= 1
            for pump in pumps:
                pump.cancel()
            await asyncio.gather(*pumps, return_exceptions=True)
            writer.close()
            if upstream is not None:
                upstream.close()

    with listen(local_family, local, socket.SOCK_STREAM) as sock:
        async with await asyncio.start_server(relay, sock=sock) as server:
            ready()
            await server.serve_forever()


def udp(local_family, local, remote_family, remote):
    clients = OrderedDict()
    with listen(local_family, local, socket.SOCK_DGRAM) as front, selectors.DefaultSelector() as selector:
        wildcard = ipaddress.ip_address(local[0]).is_unspecified
        if local_family == socket.AF_INET6:
            level, receive_info, send_info = socket.IPPROTO_IPV6, socket.IPV6_RECVPKTINFO, socket.IPV6_PKTINFO
        else:
            level, receive_info, send_info = socket.IPPROTO_IP, IP_PKTINFO, IP_PKTINFO
        if wildcard:
            front.setsockopt(level, receive_info, 1)
        selector.register(front, selectors.EVENT_READ)

        def forget(client):
            sock, _ = clients.pop(client)
            selector.unregister(sock)
            sock.close()

        def touch(client, sock):
            clients[client] = (sock, time.monotonic())
            clients.move_to_end(client)

        ready()
        try:
            while True:
                events = selector.select(timeout=1)
                for key, _ in events:
                    sock = key.fileobj
                    if sock is front:
                        try:
                            payload, ancillary, _, peer = front.recvmsg(65535, socket.CMSG_SPACE(20) if wildcard else 0)
                        except OSError:
                            continue
                        # Key by both peer and local address, and reply from the IP the peer contacted.
                        local_info = tuple((c_level, c_type, data) for c_level, c_type, data in ancillary
                                           if (c_level, c_type) == (level, send_info))
                        client = (peer, local_info)
                        if client not in clients:
                            if len(clients) >= UDP_LIMIT:
                                continue
                            upstream = socket.socket(remote_family, socket.SOCK_DGRAM)
                            try:
                                upstream.setblocking(False)
                                upstream.connect(remote)
                                selector.register(upstream, selectors.EVENT_READ, client)
                            except OSError:
                                upstream.close()
                                continue
                        else:
                            upstream, _ = clients[client]
                        touch(client, upstream)
                        try:
                            upstream.send(payload)
                        except BlockingIOError:
                            pass
                        except OSError:
                            forget(client)
                    elif key.data in clients and clients[key.data][0] is sock:
                        try:
                            payload = sock.recv(65535)
                        except BlockingIOError:
                            continue
                        except OSError:
                            forget(key.data)
                            continue
                        touch(key.data, sock)
                        try:
                            peer, local_info = key.data
                            front.sendmsg([payload], local_info, 0, peer)
                        except OSError:
                            pass
                cutoff = time.monotonic() - UDP_IDLE
                while clients:
                    client, (_, seen) = next(iter(clients.items()))
                    if seen > cutoff:
                        break
                    forget(client)
        finally:
            for client in list(clients):
                forget(client)


def main(path):
    with open(path, encoding="ascii") as config:
        protocol, local_ip, local_port, remote_ip, remote_port = config.read().strip().split()
    local_family, local = endpoint(local_ip, int(local_port))
    remote_family, remote = endpoint(remote_ip, int(remote_port))
    if local_family == remote_family or protocol not in ("tcp", "udp"):
        raise ValueError("expected a cross-family TCP or UDP forward")
    if protocol == "tcp":
        asyncio.run(tcp(local_family, local, remote_family, remote))
    else:
        udp(local_family, local, remote_family, remote)


if __name__ == "__main__":
    main(sys.argv[1])
