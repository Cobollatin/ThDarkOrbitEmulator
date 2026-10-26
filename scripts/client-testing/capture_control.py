"""mitmdump addon used by Start-CaptureSession.ps1: shut down cleanly when a stop file appears.

Windows has no SIGINT for background processes, so the capture script creates the stop file
and this addon calls ctx.master.shutdown(), which flushes the -w flow file.
"""
import asyncio
import os
from mitmproxy import ctx


class CaptureControl:
    def load(self, loader):
        loader.add_option("stop_file", str, "", "Shut down when this file exists.")

    def running(self):
        if ctx.options.stop_file:
            asyncio.get_running_loop().create_task(self._watch())

    async def _watch(self):
        while True:
            await asyncio.sleep(0.5)
            if os.path.exists(ctx.options.stop_file):
                ctx.log.info("capture_control: stop file found, shutting down")
                ctx.master.shutdown()
                return


addons = [CaptureControl()]
