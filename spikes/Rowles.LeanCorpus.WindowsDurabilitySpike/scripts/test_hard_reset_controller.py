#!/usr/bin/env python3
"""Focused host-side regression checks for the Spike 2 reset controller."""

from __future__ import annotations

import importlib.util
import io
import json
import stat
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import paramiko


CONTROLLER_PATH = Path(__file__).with_name("hard-reset-controller.py")
SPEC = importlib.util.spec_from_file_location("hard_reset_controller", CONTROLLER_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Could not load reset controller at {CONTROLLER_PATH}")
controller = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(controller)


class FakeChannel:
    def __init__(self, transport: "FakeTransport") -> None:
        self.transport = transport
        self.closed = False

    def settimeout(self, _timeout: float) -> None:
        pass

    def exec_command(self, command: str) -> None:
        self.transport.commands.append(command)

    def shutdown_write(self) -> None:
        pass

    def invoke_subsystem(self, subsystem: str) -> None:
        self.transport.subsystems.append(subsystem)

    def makefile(self, _mode: str) -> io.BytesIO:
        return io.BytesIO(b"inspection complete")

    def makefile_stderr(self, _mode: str) -> io.BytesIO:
        return io.BytesIO(b"")

    def exit_status_ready(self) -> bool:
        return True

    def recv_exit_status(self) -> int:
        return 0

    def close(self) -> None:
        self.closed = True


class FakeTransport:
    def __init__(self) -> None:
        self.open_attempts = 0
        self.commands: list[str] = []
        self.subsystems: list[str] = []
        self.channels: list[FakeChannel] = []

    def is_active(self) -> bool:
        return True

    def open_session(self, *, timeout: float) -> FakeChannel:
        self.open_attempts += 1
        if self.open_attempts == 1:
            raise paramiko.SSHException("Timeout opening channel.")
        channel = FakeChannel(self)
        self.channels.append(channel)
        return channel


class FakeClient:
    def __init__(self, transport: FakeTransport) -> None:
        self.transport = transport

    def get_transport(self) -> FakeTransport:
        return self.transport


class FakeSftp:
    instances: list["FakeSftp"] = []

    def __init__(self, channel: FakeChannel) -> None:
        self.channel = channel
        self.inspection_reads = 0
        self.instance_number = len(self.instances) + 1
        self.closed = False
        self.instances.append(self)

    def get_channel(self) -> FakeChannel:
        return self.channel

    def file(self, path: str, _mode: str) -> io.BytesIO:
        if path == "C:/control/inspection.json":
            self.inspection_reads += 1
            if self.instance_number == 1:
                raise OSError("Bad message")
            if self.inspection_reads == 1:
                raise FileNotFoundError(2, "not yet written", path)
            return io.BytesIO(json.dumps({"ContractPassed": True, "DocumentCount": 10000}).encode())
        if path == "C:/control/control.json":
            return io.BytesIO(b'{"TrialId":"trial-1"}')
        if path == "D:/data/index/seg_0":
            return io.BytesIO(b"index bytes")
        raise FileNotFoundError(2, "not found", path)

    def stat(self, path: str) -> Any:
        if path == "D:/data/index":
            return SimpleNamespace(st_mode=stat.S_IFDIR)
        raise FileNotFoundError(2, "not found", path)

    def listdir_attr(self, path: str) -> list[Any]:
        if path == "D:/data/index":
            return [SimpleNamespace(filename="seg_0", st_mode=stat.S_IFREG)]
        return []

    def close(self) -> None:
        self.closed = True


class MissingRootSftp:
    def __init__(self) -> None:
        self.channel = FakeChannel(FakeTransport())

    def get_channel(self) -> FakeChannel:
        return self.channel

    def stat(self, _path: str) -> Any:
        raise FileNotFoundError(2, "missing index root")


class HardResetControllerTests(unittest.TestCase):
    def test_reuses_sftp_and_retries_only_before_channel_allocation(self) -> None:
        previous_sftp_client = controller.paramiko.SFTPClient
        FakeSftp.instances.clear()
        controller.paramiko.SFTPClient = FakeSftp
        try:
            transport = FakeTransport()
            with tempfile.TemporaryDirectory() as temporary_directory:
                output_root = Path(temporary_directory) / "recovered"
                result, control_text, error = controller.recover_and_capture(
                    None,
                    FakeClient(transport),
                    {
                        "inspect_script": "C:\\scripts\\inspect.ps1",
                        "config_file": "C:\\control\\config.json",
                        "inspection_path": "C:\\control\\inspection.json",
                        "control_path": "C:\\control\\control.json",
                        "index_path": "D:\\data\\index",
                    },
                    output_root,
                    deadline=time.monotonic() + 5.0,
                )
                captured_index_bytes = (output_root / "index" / "seg_0").read_bytes()

            self.assertIsNone(error)
            self.assertTrue(result and result["ContractPassed"])
            self.assertEqual('{"TrialId":"trial-1"}', control_text)
            self.assertEqual(1, len(transport.commands))
            self.assertEqual(["sftp", "sftp"], transport.subsystems)
            self.assertEqual(2, len(FakeSftp.instances))
            self.assertTrue(all(sftp.closed for sftp in FakeSftp.instances))
            self.assertEqual(b"index bytes", captured_index_bytes)
        finally:
            controller.paramiko.SFTPClient = previous_sftp_client

    def test_missing_index_root_is_recorded_as_collection_error(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            evidence_root = Path(temporary_directory)
            error = controller.capture_tree_or_record_error(
                MissingRootSftp(),
                "D:\\missing-index",
                evidence_root / "index",
                evidence_root,
                deadline=time.monotonic() + 5.0,
            )

            self.assertIn("FileNotFoundError", error or "")
            self.assertIn("missing-index", (evidence_root / "index-tree-collection-error.txt").read_text())


if __name__ == "__main__":
    unittest.main()
