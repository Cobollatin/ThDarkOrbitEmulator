"""Offline checks for the two mitmproxy Redirect-mode addons."""

import importlib.util
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace


ADDON_DIR = Path(__file__).resolve().parents[2] / "_reversing" / "Mitmproxy"


def load_addon(name):
    spec = importlib.util.spec_from_file_location(name, ADDON_DIR / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


class RedirectHostTests(unittest.TestCase):
    def test_captured_hosts_route_to_local_gateway(self):
        for addon_name in ("launch_redirect", "redirect_to_local"):
            module = load_addon(addon_name)
            for original_host in (
                "170.33.13.202", "www.darkorbit.com", "sas.bpsecure.com",
                "gbl3.darkorbit.com", "gbl6.darkorbit.com", "gbl12.darkorbit.com",
            ):
                for scheme, port, target_host, target_port in (
                    ("https", 443, "localhost", 60952),
                    ("http", 80, "127.0.0.1", 60953),
                ):
                    with self.subTest(addon=addon_name, host=original_host, scheme=scheme):
                        request = SimpleNamespace(
                            host=original_host, scheme=scheme, port=port,
                            headers={"Host": original_host},
                        )
                        flow = SimpleNamespace(request=request)
                        self._redirect(module, addon_name, flow)
                        self.assertEqual(request.host, target_host)
                        self.assertEqual(request.port, target_port)
                        self.assertEqual(request.headers["Host"], f"{target_host}:{target_port}")
                        self.assertEqual(request.headers["X-Original-Host"], original_host)

    def test_unrelated_hosts_remain_upstream(self):
        for addon_name in ("launch_redirect", "redirect_to_local"):
            module = load_addon(addon_name)
            for host in ("alicdn-oss-prod.darkorbit.com", "darkorbit-22.bpsecure.com",
                         "www.google.com", "gbl0.darkorbit.com", "gbl3.darkorbit.com.evil.test",
                         "gblx.darkorbit.com", "gbl3.evil.test"):
                with self.subTest(addon=addon_name, host=host):
                    request = SimpleNamespace(host=host, scheme="https", port=443, headers={"Host": host})
                    self._redirect(module, addon_name, SimpleNamespace(request=request))
                    self.assertEqual(request.host, host)
                    self.assertEqual(request.port, 443)
                    self.assertEqual(request.headers["Host"], host)
                    self.assertNotIn("X-Original-Host", request.headers)

    def test_command_center_cdn_assets_route_locally(self):
        for addon_name in ("launch_redirect", "redirect_to_local"):
            module = load_addon(addon_name)
            for method, path, redirected in (
                ("GET", "/resources/command-center/categories.xml?__cv=version", True),
                ("GET", "/do_img/new/bg/breaking_news_bonusgg.jpg?__cv=version", True),
                ("GET", "/do_img/new/overlay/example.png", True),
                ("POST", "/do_img/new/bg/breaking_news_bonusgg.jpg", False),
                ("GET", "/do_img/global/items/ship/example.png", False),
                ("POST", "/resources/command-center/categories.xml", False),
                ("GET", "/resources/command-center/other.xml", False),
                ("GET", "/resources/command-center/categories.xml/extra", False),
            ):
                with self.subTest(addon=addon_name, method=method, path=path):
                    request = SimpleNamespace(host="darkorbit-22.bpsecure.com", scheme="https",
                                              port=443, path=path, method=method,
                                              headers={"Host": "darkorbit-22.bpsecure.com"})
                    self._redirect(module, addon_name, SimpleNamespace(request=request))
                    self.assertEqual(request.host, "localhost" if redirected else "darkorbit-22.bpsecure.com")
                    self.assertEqual(request.port, 60952 if redirected else 443)
                    if redirected:
                        self.assertEqual(request.headers["X-Original-Host"], "darkorbit-22.bpsecure.com")
                    else:
                        self.assertNotIn("X-Original-Host", request.headers)

    @staticmethod
    def _redirect(module, addon_name, flow):
        if addon_name == "launch_redirect":
            module.ctx = SimpleNamespace(options=SimpleNamespace(
                redirect=True, redirect_all=False, flow_log_file=""))
            module.LaunchAndRedirectAddon().request(flow)
        else:
            module.request(flow)


if __name__ == "__main__":
    unittest.main()
