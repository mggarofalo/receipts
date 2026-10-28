"""Internal one-shot child. Only the reserved controller may launch this worker.

No proxy discovery, redirects, retry, custom endpoint, or inherited environment.
The parent kills this child on its total deadline (including DNS and TLS).
"""
import http.client
import os
import ssl
import sys

MAX_BODY = 1_048_576
MAX_REQUEST = 262_144


def exchange(payload, key):
    if not 0 < len(payload) <= MAX_REQUEST:
        raise ValueError('request size')
    if not key or len(key) > 4096 or any(ord(c) < 33 or ord(c) > 126 for c in key):
        raise ValueError('credential format')
    # http.client does not discover proxies and never follows redirects/retries.
    connection = http.client.HTTPSConnection('api.typesafe.ai', 443, timeout=20,
                                              context=ssl.create_default_context())
    try:
        connection.request('POST', '/v1/systemone', body=payload, headers={
            'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json',
            'Accept': 'application/json', 'Accept-Encoding': 'identity',
            'Connection': 'close'})
        response = connection.getresponse()
        # Do not read error bodies: they may echo private input. Context errors,
        # redirects, rate limits and provider failures are all terminal failures.
        if response.status != 200:
            raise ValueError('provider status')
        headers = response.getheaders()
        content_types = [v for k, v in headers if k.lower() == 'content-type']
        lengths = [v for k, v in headers if k.lower() == 'content-length']
        encodings = [v for k, v in headers if k.lower() == 'content-encoding']
        transfers = [v for k, v in headers if k.lower() == 'transfer-encoding']
        if (len(content_types) != 1 or content_types[0].split(';')[0].strip().lower() != 'application/json'
                or encodings not in ([], ['identity']) or len(lengths) > 1
                or transfers not in ([], ['chunked']) or (lengths and transfers)):
            raise ValueError('response headers')
        if lengths and (not lengths[0].isascii() or not lengths[0].isdigit()
                        or not 0 < int(lengths[0]) <= MAX_BODY):
            raise ValueError('response length')
        raw = response.read(MAX_BODY + 1)
        if not 0 < len(raw) <= MAX_BODY or (lengths and len(raw) != int(lengths[0])):
            raise ValueError('response size')
        return raw
    finally:
        connection.close()


def main():
    try:
        if len(sys.argv) != 2:
            return 2
        # Explicit name provided by the controller, never searched or printed.
        key = os.environ[sys.argv[1]]
        raw = exchange(sys.stdin.buffer.read(MAX_REQUEST + 1), key)
        sys.stdout.buffer.write(raw)
        return 0
    except Exception:
        # No exception text, request, response, credential or path in diagnostics.
        return 2


if __name__ == '__main__':
    sys.exit(main())
