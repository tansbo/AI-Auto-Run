#!/usr/bin/env python3
"""AI自动跑局 遥测接收端（示例，可自托管）。

接收 CombatSolver mod opt-in 上传的匿名对局遥测 JSON（POST /telemetry），
存到 out_dir（默认 ./received-telemetry/）。生产可放到任意 VPS/反向代理后面。

用法: python tools/telemetry_receiver.py [port] [out_dir]
"""
import datetime
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8787
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parent.parent / ".local" / "received-telemetry"


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):  # noqa: N802 - http.server API
        if self.path.rstrip("/") != "/telemetry":
            self.send_response(404)
            self.end_headers()
            return
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length)
        try:
            data = json.loads(body.decode("utf-8"))
        except Exception as exc:  # noqa: BLE001 - 接收端容错
            self.send_response(400)
            self.end_headers()
            print("bad json:", exc)
            return
        OUT.mkdir(parents=True, exist_ok=True)
        ts = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
        n = 0
        while (OUT / f"{ts}-{n}.json").exists():
            n += 1
        (OUT / f"{ts}-{n}.json").write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"ok")
        print(f"saved {ts}-{n}.json seed={data.get('seed')} floors={data.get('floors')} victory={data.get('victory')}")

    def log_message(self, fmt, *args):  # 静默访问日志
        pass


if __name__ == "__main__":
    server = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    print(f"telemetry receiver on :{PORT} -> {OUT}")
    server.serve_forever()
