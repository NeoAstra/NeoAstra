from __future__ import annotations

import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]

# A WebView2 handler that returns without touching its arguments lets WebView2 apply
# its own default: a popup window, a download to the default folder, a permission
# prompt, or a browser dialog. When the view can no longer be retained, or its UI is
# already destroyed, each decision handler must settle the request the way NeoAstra's
# own default would.
SAFE_OUTCOMES = {
    "NavigationStarting": ["args->put_Cancel(TRUE)"],
    "FrameNavigationStarting": ["args->put_Cancel(TRUE)"],
    "PermissionRequested": ["args->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY)"],
    "NewWindowRequested": ["args->put_Handled(TRUE)"],
    "DownloadStarting": ["args->put_Cancel(TRUE)"],
    "BasicAuthenticationRequested": ["args->put_Cancel(TRUE)"],
    "ClientCertificateRequested": ["args->put_Cancel(TRUE)", "args->put_Handled(TRUE)"],
    "ServerCertificateErrorDetected": ["args->put_Action(COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL)"],
}

GUARD = re.compile(r"if \(!owner \|\| view->ui_destroyed\.load\(std::memory_order_acquire\)\)\s*(\{[^}]*\}|return S_OK;)")


class WindowsHandlerGuardTests(unittest.TestCase):
    def test_released_view_guards_apply_neoastra_safe_outcome(self) -> None:
        backend = (ROOT / "native/src/windows/windows_backend.cpp").read_text(encoding="utf-8")
        for event, outcomes in SAFE_OUTCOMES.items():
            with self.subTest(event=event):
                registration = backend.find(f"->add_{event}(")
                self.assertNotEqual(-1, registration, f"add_{event} registration not found")
                guard = GUARD.search(backend, registration)
                self.assertIsNotNone(guard, f"add_{event} has no released-view guard")
                assert guard is not None
                next_registration = backend.find("->add_", registration + 1)
                self.assertTrue(next_registration == -1 or guard.start() < next_registration,
                                f"the first released-view guard after add_{event} belongs to another handler")
                body = guard.group(1)
                for outcome in outcomes:
                    self.assertIn(outcome, body, f"add_{event} returns without {outcome} for a released view")
                self.assertIn("return S_OK;", body)


if __name__ == "__main__":
    unittest.main()
