#!/usr/bin/env python3
"""Run Spike 2 hard-reset trials against one explicitly disposable libvirt clone."""

from __future__ import annotations

import argparse
import base64
import csv
import hashlib
import json
import os
import platform
import re
import stat
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path, PureWindowsPath
from typing import Any

import paramiko


DEFAULT_GUEST_COMMAND_TIMEOUT_SECONDS = 60.0
CANDIDATES = ("P0", "P1", "P2")
REPRESENTATIONS = ("loose", "compound")
COMMON_FAILPOINTS = (
    "after_commit_marker_tmp_persisted",
    "after_publication_call",
    "after_directory_persist_attempt",
    "after_commit_return",
)
COMPOUND_FAILPOINTS = ("after_compound_rename", "after_loose_members_deleted")
RECOVERY_HEADER = (
    "trial_id", "termination_class", "candidate_id", "representation", "failpoint", "trial",
    "child_exit_code", "failpoint_reached", "selected_generation", "selected_generation_class",
    "document_count", "fixed_lookup_hits", "fixed_lookup_ids", "referenced_files_readable",
    "deep_validation_passed", "leftover_temporary_files", "commit_marker_inventory_json",
    "contract_passed", "error", "index_path", "log_path",
)
PROTECTED_DOMAINS = {
    "leancorpus-windows2025",
    "leancorpus-windows2025-spike1",
    "leancorpus-windows2025-spike1b-replacement",
}
PROTECTED_IMAGES = {
    "leancorpus-windows2025.qcow2",
    "leancorpus-windows2025-spike1.qcow2",
    "leancorpus-windows2025-spike1b-replacement.qcow2",
}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def powershell_quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def run_command(command: list[str], *, check: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if check and result.returncode:
        raise RuntimeError(
            f"Command failed ({result.returncode}): {command!r}\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}"
        )
    return result


def run_virsh(uri: str, *arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return run_command(["virsh", "-c", uri, *arguments], check=check)


def domain_state(uri: str, domain: str) -> str:
    return run_virsh(uri, "domstate", domain).stdout.strip().lower()


def domain_xml(uri: str, domain: str) -> str:
    return run_virsh(uri, "dumpxml", domain).stdout


def disk_configuration(xml_text: str) -> dict[str, Any]:
    root = ET.fromstring(xml_text)
    disks = []
    for disk in root.findall("./devices/disk"):
        if disk.get("device") != "disk":
            continue
        driver = disk.find("driver")
        source = disk.find("source")
        target = disk.find("target")
        backing = disk.find("backingStore")
        disks.append({
            "type": disk.get("type", "unknown"),
            "device": disk.get("device", "unknown"),
            "driver_type": driver.get("type", "unknown") if driver is not None else "unknown",
            "cache_mode": driver.get("cache", "unknown") if driver is not None else "unknown",
            "io_mode": driver.get("io", "unknown") if driver is not None else "unknown",
            "discard_mode": driver.get("discard", "unknown") if driver is not None else "unknown",
            "detect_zeroes_mode": driver.get("detect_zeroes", "unknown") if driver is not None else "unknown",
            "source_file": source.get("file", "unknown") if source is not None else "unknown",
            "source_dev": source.get("dev", "unknown") if source is not None else "unknown",
            "source_name": source.get("name", "unknown") if source is not None else "unknown",
            "target_dev": target.get("dev", "unknown") if target is not None else "unknown",
            "disk_bus": target.get("bus", "unknown") if target is not None else "unknown",
            "backing_store_type": backing.get("type", "unknown") if backing is not None else "unknown",
        })
    if not disks:
        raise RuntimeError("The disposable domain has no configured guest disks.")
    if any(disk["cache_mode"].lower() == "unsafe" for disk in disks):
        raise RuntimeError("Hard-reset evidence is prohibited because the domain uses cache='unsafe'.")
    for disk in disks:
        source_name = Path(disk["source_file"]).name.lower() if disk["source_file"] != "unknown" else ""
        if source_name in PROTECTED_IMAGES:
            raise RuntimeError(f"Refusing reset because the guest disk points directly at protected image '{source_name}'.")
    return {"domain_name": root.findtext("name", "unknown"), "domain_type": root.get("type", "unknown"), "disks": disks}


def host_storage(path: str) -> tuple[str, str]:
    if not path or path == "unknown":
        return "unknown", "unknown"
    result = run_command(["findmnt", "-n", "-o", "FSTYPE,SOURCE", "-T", path], check=False)
    if result.returncode:
        return "unknown", "unknown"
    parts = result.stdout.strip().split(None, 1)
    if len(parts) != 2:
        return "unknown", "unknown"
    return parts[0], parts[1]


def host_os() -> str:
    try:
        data = Path("/etc/os-release").read_text(encoding="utf-8")
        for line in data.splitlines():
            if line.startswith("PRETTY_NAME="):
                return line.split("=", 1)[1].strip().strip('"')
    except OSError:
        pass
    return platform.platform()


def load_password(path: Path) -> str:
    lines = [line for line in path.read_text(encoding="utf-8").splitlines() if line != ""]
    if len(lines) == 1:
        password = lines[0]
    elif (
        len(lines) == 3
        and lines[0] == lines[1]
        and lines[2].strip().startswith("Administrator@")
        and lines[2].strip().endswith("'s password:")
    ):
        password = lines[0]
    else:
        raise RuntimeError("Administrator password file must contain one value or a duplicated SSH prompt transcript.")
    if not password or password.isspace():
        raise RuntimeError("The Administrator password file is empty.")
    return password


def verify_domain_name(domain: str) -> None:
    if domain in PROTECTED_DOMAINS or not re.fullmatch(r"leancorpus-windows2025-spike2-[a-z0-9-]+", domain):
        raise RuntimeError(
            "Refusing VM reset: --domain must be a disposable 'leancorpus-windows2025-spike2-*' clone."
        )


class PinnedHostKey(paramiko.MissingHostKeyPolicy):
    def __init__(self, expected: str) -> None:
        self.expected = expected

    def missing_host_key(self, client: paramiko.SSHClient, hostname: str, key: paramiko.PKey) -> None:
        fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode("ascii").rstrip("=")
        if fingerprint != self.expected:
            raise paramiko.SSHException(f"Guest host key fingerprint mismatch: observed {fingerprint}.")
        client.get_host_keys().add(hostname, key.get_name(), key)


class Guest:
    def __init__(self, host: str, port: int, username: str, password: str, host_key: str) -> None:
        self.host = host
        self.port = port
        self.username = username
        self.password = password
        self.host_key = host_key

    def connect(self, *, timeout_seconds: float = 15.0) -> paramiko.SSHClient:
        client = paramiko.SSHClient()
        client.set_missing_host_key_policy(PinnedHostKey(self.host_key))
        connection_timeout = max(0.1, min(10.0, timeout_seconds))
        handshake_timeout = max(0.1, min(15.0, timeout_seconds))
        client.connect(self.host, port=self.port, username=self.username, password=self.password,
                       timeout=connection_timeout, banner_timeout=handshake_timeout,
                       auth_timeout=handshake_timeout,
                       allow_agent=False, look_for_keys=False)
        return client

    @staticmethod
    def ps_command(script: str) -> str:
        encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
        return f"pwsh.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}"

    @staticmethod
    def run_ps(client: paramiko.SSHClient, script: str, *, check: bool = True,
               timeout_seconds: float = DEFAULT_GUEST_COMMAND_TIMEOUT_SECONDS) -> tuple[int, str, str]:
        _, stdout, stderr = client.exec_command(Guest.ps_command(script), timeout=timeout_seconds)
        out = stdout.read().decode("utf-8", errors="replace")
        err = stderr.read().decode("utf-8", errors="replace")
        status = stdout.channel.recv_exit_status()
        if check and status:
            raise RuntimeError(f"Guest PowerShell returned {status}.\nstdout:\n{out}\nstderr:\n{err}")
        return status, out, err

    @staticmethod
    def start_waiting_child(client: paramiko.SSHClient, script: str) -> tuple[dict[str, Any], Any, Any]:
        _, stdout, stderr = client.exec_command(Guest.ps_command(script), timeout=60)
        stdout.channel.settimeout(60)
        line = stdout.readline()
        if not line:
            error = stderr.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"Hard-reset child launcher did not acknowledge its start: {error.strip()}")
        try:
            return json.loads(line), stdout, stderr
        except json.JSONDecodeError as parse_error:
            raise RuntimeError(f"Hard-reset child launcher returned invalid start metadata: {line!r}") from parse_error

    @staticmethod
    def sftp(client: paramiko.SSHClient) -> paramiko.SFTPClient:
        return client.open_sftp()


def remote_path(path: str) -> str:
    return PureWindowsPath(path).as_posix()


def ensure_remote_directories(guest: Guest, client: paramiko.SSHClient, *paths: str) -> None:
    expressions = ";".join(f"New-Item -ItemType Directory -Path {powershell_quote(path)} -Force | Out-Null" for path in paths)
    guest.run_ps(client, f"$ErrorActionPreference='Stop'; {expressions}")


def put_text(client: paramiko.SSHClient, path: str, content: str) -> None:
    with Guest.sftp(client) as sftp:
        with sftp.file(remote_path(path), "w") as output:
            output.write(content.encode("utf-8"))


def get_text(client: paramiko.SSHClient, path: str, *, timeout_seconds: float | None = None) -> str:
    with Guest.sftp(client) as sftp:
        if timeout_seconds is not None:
            sftp.get_channel().settimeout(timeout_seconds)
        with sftp.file(remote_path(path), "r") as source:
            return source.read().decode("utf-8", errors="replace")


def get_text_if_exists(client: paramiko.SSHClient, path: str, *,
                       timeout_seconds: float | None = None) -> str | None:
    try:
        return get_text(client, path, timeout_seconds=timeout_seconds)
    except OSError:
        return None


def _set_sftp_timeout(sftp: paramiko.SFTPClient, deadline: float,
                      maximum_seconds: float | None = None) -> float:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError("The hard-reset inspection deadline expired during SFTP collection.")
    timeout = min(remaining, maximum_seconds) if maximum_seconds is not None else remaining
    sftp.get_channel().settimeout(timeout)
    return timeout


def _read_sftp_text_if_exists(sftp: paramiko.SFTPClient, path: str, deadline: float,
                              *, maximum_operation_seconds: float | None = None) -> str | None:
    _set_sftp_timeout(sftp, deadline, maximum_operation_seconds)
    try:
        with sftp.file(remote_path(path), "r") as source:
            chunks: list[bytes] = []
            while True:
                _set_sftp_timeout(sftp, deadline, maximum_operation_seconds)
                chunk = source.read(64 * 1024)
                if not chunk:
                    return b"".join(chunks).decode("utf-8", errors="replace")
                chunks.append(chunk)
    except FileNotFoundError:
        return None


def get_tree(sftp: paramiko.SFTPClient, remote_root: str, local_root: Path, *, deadline: float) -> None:
    _set_sftp_timeout(sftp, deadline)
    try:
        root_attributes = sftp.stat(remote_path(remote_root))
    except FileNotFoundError as error:
        raise FileNotFoundError(error.errno, error.strerror or "Recovered index root is missing", remote_root) from error
    if not stat.S_ISDIR(root_attributes.st_mode):
        raise NotADirectoryError(f"Recovered index root is not a directory: {remote_root}")
    local_root.mkdir(parents=True, exist_ok=False)
    _get_tree_children(sftp, remote_path(remote_root), local_root, deadline=deadline)


def capture_tree_or_record_error(sftp: paramiko.SFTPClient, remote_root: str,
                                local_root: Path, evidence_root: Path, *, deadline: float) -> str | None:
    try:
        get_tree(sftp, remote_root, local_root, deadline=deadline)
        return None
    except (OSError, paramiko.SSHException, EOFError, TimeoutError) as error:
        message = f"{type(error).__name__}: {error}"
        (evidence_root / "index-tree-collection-error.txt").write_text(message + "\n", encoding="utf-8")
        return message


def _get_tree_children(sftp: paramiko.SFTPClient, remote_root: str, local_root: Path, *, deadline: float) -> None:
    _set_sftp_timeout(sftp, deadline)
    for entry in sftp.listdir_attr(remote_root):
        source = str(PureWindowsPath(remote_root) / entry.filename).replace("\\", "/")
        destination = local_root / entry.filename
        if stat.S_ISDIR(entry.st_mode):
            destination.mkdir(parents=True, exist_ok=False)
            _get_tree_children(sftp, source, destination, deadline=deadline)
        else:
            destination.parent.mkdir(parents=True, exist_ok=True)
            with sftp.file(source, "r") as remote_file, destination.open("wb") as local_file:
                while True:
                    _set_sftp_timeout(sftp, deadline)
                    chunk = remote_file.read(1024 * 1024)
                    if not chunk:
                        break
                    local_file.write(chunk)


def plan_trials() -> list[dict[str, Any]]:
    plan: list[dict[str, Any]] = []
    for candidate in CANDIDATES:
        for representation in REPRESENTATIONS:
            failpoints = [point for point in COMMON_FAILPOINTS
                          if not (candidate == "P1" and point == "after_directory_persist_attempt")]
            if representation == "compound":
                failpoints.extend(COMPOUND_FAILPOINTS)
            for failpoint in failpoints:
                for trial in range(1, 4):
                    trial_id = f"hard-reset-{candidate}-{representation}-{failpoint}-trial-{trial}"
                    plan.append({"trial_id": trial_id, "candidate": candidate,
                                 "representation": representation, "failpoint": failpoint, "trial": trial})
    if len(plan) != 84:
        raise RuntimeError(f"Hard-reset matrix cardinality error: expected 84 rows, got {len(plan)}.")
    return plan


def write_csv(path: Path, header: tuple[str, ...], rows: list[tuple[Any, ...]]) -> None:
    with path.open("x", newline="", encoding="utf-8") as output:
        writer = csv.writer(output, lineterminator="\n")
        writer.writerow(header)
        writer.writerows(rows)


def wait_for_guest(guest: Guest, timeout_seconds: int, interval_seconds: float = 2.0, *,
                   deadline: float | None = None) -> paramiko.SSHClient:
    if deadline is None:
        deadline = time.monotonic() + timeout_seconds
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        client: paramiko.SSHClient | None = None
        try:
            remaining = deadline - time.monotonic()
            client = guest.connect(timeout_seconds=remaining)
            return client
        except Exception as error:  # Connection refusal is expected during guest boot.
            last_error = error
            if client is not None:
                client.close()
            remaining = deadline - time.monotonic()
            if remaining > 0:
                time.sleep(min(interval_seconds, remaining))
    raise TimeoutError(f"Guest SSH did not return within {timeout_seconds}s: {last_error}")


def _channel_request_before_deadline(channel: Any, request: Any, *, deadline: float, description: str) -> Any:
    completed = threading.Event()
    result: dict[str, Any] = {}

    def invoke() -> None:
        try:
            result["value"] = request()
        except BaseException as error:
            result["error"] = error
        finally:
            completed.set()

    worker = threading.Thread(target=invoke, name=f"spike2-{description}", daemon=True)
    worker.start()
    remaining = deadline - time.monotonic()
    if remaining <= 0 or not completed.wait(remaining):
        channel.close()
        raise TimeoutError(f"SSH {description} did not complete before the inspection deadline.")
    if "error" in result:
        raise result["error"]
    return result.get("value")


def _open_session_with_retry(client: paramiko.SSHClient, *, deadline: float,
                             description: str) -> Any:
    transport = client.get_transport()
    if transport is None or not transport.is_active():
        raise paramiko.SSHException("The guest SSH transport is not active.")
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        remaining = deadline - time.monotonic()
        try:
            return transport.open_session(timeout=min(10.0, remaining))
        except (OSError, paramiko.SSHException, EOFError, TimeoutError) as error:
            last_error = error
            remaining = deadline - time.monotonic()
            if remaining > 0:
                time.sleep(min(0.25, remaining))
    raise TimeoutError(f"Could not open the SSH {description} channel before the inspection deadline: {last_error}")


def _open_inspection_channel(client: paramiko.SSHClient, command: str, *, deadline: float) -> tuple[Any, Any, Any]:
    channel = _open_session_with_retry(client, deadline=deadline, description="inspection command")
    try:
        channel.settimeout(max(0.1, deadline - time.monotonic()))
        _channel_request_before_deadline(
            channel, lambda: channel.exec_command(command), deadline=deadline,
            description="inspection command request")
        channel.shutdown_write()
        return channel, channel.makefile("rb"), channel.makefile_stderr("rb")
    except BaseException:
        channel.close()
        raise


def _open_sftp_with_deadline(client: paramiko.SSHClient, *, deadline: float) -> paramiko.SFTPClient:
    channel = _open_session_with_retry(client, deadline=deadline, description="SFTP")
    try:
        channel.settimeout(max(0.1, deadline - time.monotonic()))

        def start_sftp() -> paramiko.SFTPClient:
            channel.invoke_subsystem("sftp")
            channel.settimeout(max(0.1, deadline - time.monotonic()))
            return paramiko.SFTPClient(channel)

        return _channel_request_before_deadline(
            channel, start_sftp, deadline=deadline, description="SFTP subsystem")
    except BaseException:
        channel.close()
        raise


def _read_inspection_output(channel: Any, stdout: Any, stderr: Any, *, deadline: float) -> tuple[int, str, str]:
    status = channel.recv_exit_status()
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError("The hard-reset inspection deadline expired while collecting command output.")
    channel.settimeout(remaining)
    output = stdout.read().decode("utf-8", errors="replace")
    error = stderr.read().decode("utf-8", errors="replace")
    return status, output, error


def recover_and_capture(guest: Guest, client: paramiko.SSHClient, config: dict[str, Any],
                        output_root: Path, *, deadline: float) -> tuple[dict[str, Any] | None, str | None, str | None]:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError("The hard-reset recovery deadline expired before inspection started.")
    inspect_command = (
        f"$ErrorActionPreference='Stop'; & {powershell_quote(config['inspect_script'])} "
        f"-ConfigFile {powershell_quote(config['config_file'])}"
    )
    stdout: Any | None = None
    stderr: Any | None = None
    channel: Any | None = None
    sftp: paramiko.SFTPClient | None = None
    inspection_text: str | None = None
    remote_exit: int | None = None
    remote_error = ""
    remote_output = ""
    last_error: Exception | None = None
    sftp_reconnects = 0
    sftp_retry_pending = False
    try:
        channel, stdout, stderr = _open_inspection_channel(
            client, Guest.ps_command(inspect_command), deadline=deadline)
    except (OSError, paramiko.SSHException, EOFError, TimeoutError) as error:
        last_error = error

    while time.monotonic() < deadline:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        try:
            if sftp is None:
                sftp = _open_sftp_with_deadline(client, deadline=deadline)
            candidate = _read_sftp_text_if_exists(
                sftp, config["inspection_path"], deadline, maximum_operation_seconds=5.0)
        except (OSError, paramiko.SSHException, EOFError, TimeoutError) as error:
            last_error = error
            if sftp is not None:
                sftp.close()
                sftp = None
            if sftp_reconnects >= 1:
                break
            sftp_reconnects += 1
            sftp_retry_pending = True
        else:
            sftp_retry_pending = False
            if candidate:
                try:
                    json.loads(candidate)
                except json.JSONDecodeError as error:
                    last_error = error
                else:
                    inspection_text = candidate
                    break

        if remote_exit is None and channel is not None and channel.exit_status_ready():
            try:
                remote_exit, remote_output, remote_error = _read_inspection_output(
                    channel, stdout, stderr, deadline=deadline)
            except (OSError, paramiko.SSHException, EOFError, TimeoutError) as error:
                last_error = last_error or error
            if inspection_text is None and not sftp_retry_pending:
                break

        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(0.25, remaining))

    output_root.mkdir(parents=True, exist_ok=False)
    control_text: str | None = None
    control_error: str | None = None
    if sftp is not None and time.monotonic() < deadline:
        try:
            control_text = _read_sftp_text_if_exists(
                sftp, config["control_path"], deadline, maximum_operation_seconds=5.0)
        except (OSError, paramiko.SSHException, EOFError, TimeoutError) as error:
            control_error = f"{type(error).__name__}: {error}"
            last_error = last_error or error
    if inspection_text is not None:
        (output_root / "recovery-inspection.json").write_text(inspection_text, encoding="utf-8")
    if control_text is not None:
        (output_root / "reset-control.json").write_text(control_text, encoding="utf-8")
    if sftp is None:
        tree_error = "SFTP session unavailable before index-tree collection."
        (output_root / "index-tree-collection-error.txt").write_text(tree_error + "\n", encoding="utf-8")
    else:
        tree_error = capture_tree_or_record_error(
            sftp, config["index_path"], output_root / "index", output_root, deadline=deadline)
    if channel is not None and channel.exit_status_ready() and remote_exit is None:
        try:
            remote_exit, remote_output, remote_error = _read_inspection_output(
                channel, stdout, stderr, deadline=deadline)
        except (OSError, paramiko.SSHException, EOFError, TimeoutError) as error:
            last_error = last_error or error
    if channel is not None:
        channel.close()
    if sftp is not None:
        sftp.close()
    if remote_output:
        (output_root / "inspection-command.stdout.txt").write_text(remote_output, encoding="utf-8")
    if remote_error:
        (output_root / "inspection-command.stderr.txt").write_text(remote_error, encoding="utf-8")
    if inspection_text is None:
        if last_error is None:
            last_error = TimeoutError("Recovery inspection JSON did not appear before the 60-second command deadline.")
        error_parts = ["recovery_inspection_missing", f"remote_exit={remote_exit}", str(last_error)]
        if remote_error.strip():
            error_parts.append(remote_error.strip())
        if tree_error:
            error_parts.append(f"index_tree_collection_failed: {tree_error}")
        if control_error:
            error_parts.append(f"control_record_collection_failed: {control_error}")
        return None, control_text, "; ".join(error_parts)
    if tree_error:
        return json.loads(inspection_text), control_text, f"index_tree_collection_failed: {tree_error}"
    if control_error:
        return json.loads(inspection_text), control_text, f"control_record_collection_failed: {control_error}"
    if remote_exit not in (None, 0):
        return json.loads(inspection_text), control_text, f"recovery_inspect_exit_{remote_exit}: {remote_error.strip()}"
    return json.loads(inspection_text), control_text, None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--domain", required=True)
    parser.add_argument("--snapshot", required=True)
    parser.add_argument("--guest-host", required=True)
    parser.add_argument("--guest-port", type=int, default=22)
    parser.add_argument("--username", default="Administrator")
    parser.add_argument("--host-key-sha256", required=True)
    parser.add_argument("--password-file", default="/home/jordan/windows-vm/administrator-password")
    parser.add_argument("--experiment-sha", required=True)
    parser.add_argument("--recovery-dataset", required=True,
                        help="guest path to the prepared 10,000-record 90,000..99,999 DataForge slice")
    parser.add_argument("--data-root", default="D:\\Spike2Data")
    parser.add_argument("--control-root", default="C:\\Spike2Control")
    parser.add_argument("--checkout", default="C:\\source\\leancorpus")
    parser.add_argument("--evidence", required=True, help="local_windows_vm evidence directory")
    parser.add_argument("--ssh-timeout", type=int, default=240)
    parser.add_argument("--failpoint-timeout", type=int, default=900)
    parser.add_argument("--libvirt-uri", default="qemu:///system")
    args = parser.parse_args()

    verify_domain_name(args.domain)
    if not re.fullmatch(r"[0-9a-fA-F]{40}", args.experiment_sha):
        raise RuntimeError("--experiment-sha must be the frozen 40-character commit SHA.")
    if not args.host_key_sha256.startswith("SHA256:"):
        raise RuntimeError("--host-key-sha256 must be an OpenSSH SHA256 fingerprint.")
    for path in (args.data_root, args.control_root, args.checkout, args.recovery_dataset):
        if not PureWindowsPath(path).is_absolute():
            raise RuntimeError(f"Guest path must be absolute: {path}")
    if PureWindowsPath(args.data_root).drive.lower() == PureWindowsPath(args.control_root).drive.lower():
        raise RuntimeError("The control records must be outside the disposable NTFS index volume.")

    password_path = Path(args.password_file)
    password_stat = password_path.stat()
    if os.name != "nt" and password_stat.st_mode & 0o077:
        raise RuntimeError("The Administrator password file must be mode 600 or stricter.")
    password = load_password(password_path)

    output = Path(args.evidence).expanduser().resolve()
    if not output.is_dir():
        raise RuntimeError(f"Evidence directory must already exist: {output}")
    root = output / "reset-control" / "hard-reset"
    root.mkdir(parents=True, exist_ok=False)
    logs = output / "logs"
    logs.mkdir(parents=True, exist_ok=True)
    commands_path = root / "controller-events.jsonl"
    order_path = root / "hard-reset-order.csv"
    rows_path = root / "recovery-trials.csv"
    plan = plan_trials()
    with order_path.open("x", newline="", encoding="utf-8") as order_file:
        writer = csv.writer(order_file, lineterminator="\n")
        writer.writerow(("trial_id", "candidate_id", "representation", "failpoint", "trial"))
        for item in plan:
            writer.writerow((item["trial_id"], item["candidate"], item["representation"], item["failpoint"], item["trial"]))

    def log_event(event: dict[str, Any]) -> None:
        event["recorded_utc"] = utc_now()
        with commands_path.open("a", encoding="utf-8") as log:
            log.write(json.dumps(event, sort_keys=True) + "\n")

    xml_text = domain_xml(args.libvirt_uri, args.domain)
    disk = disk_configuration(xml_text)
    snapshot_names = run_virsh(args.libvirt_uri, "snapshot-list", args.domain, "--name").stdout.split()
    if args.snapshot not in snapshot_names:
        raise RuntimeError(f"Fixed snapshot '{args.snapshot}' is not present on disposable domain '{args.domain}'.")
    image_path = next((disk_row["source_file"] for disk_row in disk["disks"] if disk_row["source_file"] != "unknown"), "unknown")
    filesystem, host_storage_device = host_storage(image_path)
    host_metadata = {
        "host_os": host_os(), "hypervisor": "QEMU/KVM via libvirt", "host_filesystem": filesystem,
        "host_storage_device": host_storage_device, "disk_configuration": disk,
        "snapshot_id": args.snapshot, "reset_method": "virsh reset (abrupt domain reset)",
    }
    with (logs / "hypervisor-disk-config.txt").open("x", encoding="utf-8") as config_log:
        config_log.write("libvirt domain XML at controller start\n" + xml_text +
                         "\nHost storage inspection\n" + json.dumps(host_metadata, indent=2, sort_keys=True) + "\n")
    with (root / "host-hypervisor-metadata.json").open("x", encoding="utf-8") as host_log:
        host_log.write(json.dumps(host_metadata, indent=2) + "\n")

    guest = Guest(args.guest_host, args.guest_port, args.username, password, args.host_key_sha256)
    script_root = str(PureWindowsPath(args.checkout) / "spikes" / "Rowles.LeanCorpus.WindowsDurabilitySpike" / "scripts")
    assembly = str(PureWindowsPath(args.checkout) / "artifacts" / "bin" /
                   "Rowles.LeanCorpus.WindowsDurabilitySpike" / "release_net10.0" /
                   "Rowles.LeanCorpus.WindowsDurabilitySpike.dll")
    semantics_local = output / "publication-semantics.md"
    if not semantics_local.is_file():
        raise RuntimeError(f"Publication semantics evidence is missing: {semantics_local}")
    semantics_remote = str(PureWindowsPath(args.control_root) / "publication-semantics.md")
    environment_vars: dict[str, str] | None = None
    output_rows: list[tuple[Any, ...]] = []

    with rows_path.open("x", newline="", encoding="utf-8") as rows_file:
        csv_writer = csv.writer(rows_file, lineterminator="\n")
        csv_writer.writerow(RECOVERY_HEADER)
        for item in plan:
            trial_id = item["trial_id"]
            candidate = item["candidate"]
            representation = item["representation"]
            failpoint = item["failpoint"]
            trial_root = root / "trials" / trial_id
            trial_root.mkdir(parents=True, exist_ok=False)
            revert_command = ["snapshot-revert", args.domain, args.snapshot, "--running", "--force"]
            before_revert = domain_state(args.libvirt_uri, args.domain)
            revert_started = utc_now()
            revert = run_virsh(args.libvirt_uri, *revert_command)
            after_revert = domain_state(args.libvirt_uri, args.domain)
            log_event({"trial_id": trial_id, "command": ["virsh", "-c", args.libvirt_uri, *revert_command],
                       "started_utc": revert_started, "completed_utc": utc_now(),
                       "vm_state_before": before_revert, "vm_state_after": after_revert,
                       "stdout": revert.stdout.strip(), "stderr": revert.stderr.strip()})
            if after_revert != "running":
                raise RuntimeError(f"Snapshot revert did not leave the clone running; state={after_revert}.")

            client = wait_for_guest(guest, args.ssh_timeout)
            child_session: paramiko.SSHClient | None = None
            child_stdout: Any | None = None
            child_stderr: Any | None = None
            try:
                verify_script = str(PureWindowsPath(script_root) / "verify-hard-reset-prerequisites.ps1")
                verify_call = (
                    "$ErrorActionPreference='Stop'; & " + powershell_quote(verify_script) +
                    " -ExpectedExperimentSha " + powershell_quote(args.experiment_sha) +
                    " -RecoveryDataset " + powershell_quote(args.recovery_dataset)
                )
                _, preflight_text, _ = guest.run_ps(client, verify_call)
                preflight = json.loads(preflight_text)
                if preflight.get("guest_checkout_clean") is not True or preflight.get("guest_checkout_sha", "").lower() != args.experiment_sha.lower():
                    raise RuntimeError("Guest checkout provenance failed the hard-reset preflight.")

                collect_script = str(PureWindowsPath(script_root) / "collect-windows-environment.ps1")
                index_disk = next((row for row in disk["disks"] if row["target_dev"] == "hdd"), None)
                if index_disk is None:
                    raise RuntimeError("The disposable NTFS index disk is missing from the clone configuration.")
                collect_arguments = {
                    "ExperimentSha": args.experiment_sha,
                    "Hypervisor": host_metadata["hypervisor"],
                    "HostOs": host_metadata["host_os"],
                    "HostStorage": host_storage_device if host_storage_device != "unknown" else "unknown",
                    "HostCachePolicy": index_disk["cache_mode"],
                    "GuestCachePolicy": "unknown",
                    "VirtualDiskType": index_disk["driver_type"],
                    "VirtualController": index_disk["target_dev"],
                    "DiskBus": index_disk["disk_bus"],
                    "LibvirtCacheMode": index_disk["cache_mode"],
                    "LibvirtIoMode": index_disk["io_mode"],
                    "DiscardMode": index_disk["discard_mode"],
                    "DetectZeroesMode": index_disk["detect_zeroes_mode"],
                    "BackingStoreType": index_disk["backing_store_type"],
                    "BackingStore": index_disk["source_file"],
                    "HostFilesystem": filesystem,
                    "ResetMethod": "virsh reset (abrupt domain reset)",
                    "SnapshotId": args.snapshot,
                }
                command = "$ErrorActionPreference='Stop'; & " + powershell_quote(collect_script)
                for key, value in collect_arguments.items():
                    command += f" -{key} {powershell_quote(str(value))}"
                _, environment_text, _ = guest.run_ps(client, command)
                environment = json.loads(environment_text)
                current_vars = {
                    "SPIKE_EXPERIMENT_SHA": args.experiment_sha,
                    "SPIKE_WINDOWS_EDITION": environment.get("windows_edition", "unknown"),
                    "SPIKE_WINDOWS_BUILD": environment.get("windows_build", "unknown"),
                    "SPIKE_CPU_MODEL": environment.get("cpu_model", "unknown"),
                    "SPIKE_RAM_BYTES": environment.get("ram_bytes", "unknown"),
                    "SPIKE_POWER_MODE": environment.get("power_mode", "unknown"),
                    "SPIKE_DEFENDER_STATE": environment.get("defender_state", "unknown"),
                    "SPIKE_FILTER_STATE": environment.get("other_filter_driver_or_antivirus_state_if_known", "unknown"),
                    "DOTNET_SDK_VERSION": environment.get("dotnet_sdk", "unknown"),
                    "SPIKE_HYPERVISOR": host_metadata["hypervisor"],
                    "SPIKE_HOST_OS": host_metadata["host_os"],
                    "SPIKE_HOST_STORAGE": environment.get("host_storage_description", "unknown"),
                    "SPIKE_HOST_CACHE_POLICY": environment.get("host_cache_policy_if_known", "unknown"),
                    "SPIKE_GUEST_CACHE_POLICY": environment.get("guest_write_cache_policy_if_known", "unknown"),
                    "SPIKE_VIRTUAL_DISK_TYPE": environment.get("virtual_disk_type", "unknown"),
                    "SPIKE_VIRTUAL_CONTROLLER": environment.get("virtual_controller", "unknown"),
                    "SPIKE_DISK_BUS": environment.get("disk_bus", "unknown"),
                    "SPIKE_LIBVIRT_CACHE_MODE": environment.get("libvirt_cache_mode", "unknown"),
                    "SPIKE_LIBVIRT_IO_MODE": environment.get("libvirt_io_mode", "unknown"),
                    "SPIKE_DISCARD_MODE": environment.get("discard_mode", "unknown"),
                    "SPIKE_DETECT_ZEROES_MODE": environment.get("detect_zeroes_mode", "unknown"),
                    "SPIKE_BACKING_STORE_TYPE": environment.get("backing_store_type", "unknown"),
                    "SPIKE_BACKING_STORE": environment.get("backing_store_path_or_identifier", "unknown"),
                    "SPIKE_HOST_FILESYSTEM": environment.get("host_filesystem_for_vm_image", "unknown"),
                    "SPIKE_RESET_METHOD": environment.get("reset_method", "unknown"),
                    "SPIKE_SNAPSHOT_ID": environment.get("snapshot_id", "unknown"),
                }
                if environment_vars is None:
                    environment_vars = current_vars
                    (root / "environment.json").write_text(json.dumps(environment, indent=2) + "\n", encoding="utf-8")
                elif environment.get("defender_state") != json.loads((root / "environment.json").read_text())["defender_state"]:
                    raise RuntimeError("Defender state changed between hard-reset trials; recovery measurements stopped.")

                control_dir = str(PureWindowsPath(args.control_root) / "trials")
                inspection_dir = str(PureWindowsPath(args.control_root) / "inspections")
                ensure_remote_directories(guest, client, args.data_root, control_dir, inspection_dir)
                put_text(client, semantics_remote, semantics_local.read_text(encoding="utf-8"))
                neutrality_path = str(PureWindowsPath(args.control_root) / "instrumentation-audit.md")
                neutrality_json = str(PureWindowsPath(args.control_root) / "instrumentation-audit.json")
                if not (root / "instrumentation-audit.json").exists():
                    _, _, _ = guest.run_ps(client,
                        f"$ErrorActionPreference='Stop'; & dotnet {powershell_quote(assembly)} "
                        f"validate-observation-neutrality --output {powershell_quote(neutrality_path)}")
                    (root / "instrumentation-audit.md").write_text(get_text(client, neutrality_path), encoding="utf-8")
                    (root / "instrumentation-audit.json").write_text(get_text(client, neutrality_json), encoding="utf-8")
                candidate_validation_remote = str(PureWindowsPath(args.control_root) / "candidate-validation.json")
                if not (root / "candidate-validation.json").exists():
                    _, _, _ = guest.run_ps(client,
                        f"$ErrorActionPreference='Stop'; & dotnet {powershell_quote(assembly)} "
                        f"validate-publication-candidates --data-root {powershell_quote(args.data_root)} "
                        f"--output {powershell_quote(candidate_validation_remote)}")
                    (root / "candidate-validation.json").write_text(get_text(client, candidate_validation_remote), encoding="utf-8")
                candidate_validation = json.loads((root / "candidate-validation.json").read_text(encoding="utf-8"))
                if candidate_validation.get("passed") is not True:
                    raise RuntimeError("Publication candidate correctness gate failed before hard-reset collection.")

                if environment_vars is None:
                    raise RuntimeError("Environment metadata was not collected.")
                env_copy = dict(environment_vars)
                config_file = str(PureWindowsPath(args.control_root) / "trials" / f"{trial_id}.json")
                index_path = str(PureWindowsPath(args.data_root) / trial_id)
                control_path = str(PureWindowsPath(args.control_root) / "trials" / f"{trial_id}.control.json")
                inspection_path = str(PureWindowsPath(args.control_root) / "inspections" / f"{trial_id}.json")
                config = {
                    "ExperimentSha": args.experiment_sha,
                    "Candidate": candidate,
                    "Representation": representation,
                    "Failpoint": failpoint,
                    "TrialId": trial_id,
                    "TrialIndex": index_path,
                    "ControlPath": control_path,
                    "DatasetPath": args.recovery_dataset,
                    "AssemblyPath": assembly,
                    "Environment": env_copy,
                    "AfterCommitReturn": failpoint == "after_commit_return",
                    "InspectionPath": inspection_path,
                    "config_file": config_file,
                    "index_path": index_path,
                    "control_path": control_path,
                    "inspection_path": inspection_path,
                    "inspect_script": str(PureWindowsPath(script_root) / "inspect-hard-reset-trial.ps1"),
                    "ChildStdoutPath": str(PureWindowsPath(args.control_root) / "trials" / f"{trial_id}.stdout.txt"),
                    "ChildStderrPath": str(PureWindowsPath(args.control_root) / "trials" / f"{trial_id}.stderr.txt"),
                }
                put_text(client, config_file, json.dumps(config, separators=(",", ":")))
                launcher = str(PureWindowsPath(script_root) / "start-hard-reset-child.ps1")
                started, child_stdout, child_stderr = guest.start_waiting_child(client,
                    f"$ErrorActionPreference='Stop'; & {powershell_quote(launcher)} "
                    f"-ConfigFile {powershell_quote(config_file)} -WaitForExit")
                pid = int(started["process_id"])
                log_event({"trial_id": trial_id, "event": "child_started", "pid": pid,
                           "started_utc": started.get("started_utc"), "failpoint": failpoint,
                           "index_path": index_path, "control_path": control_path})
                child_session = client
                client = None
            finally:
                if client is not None:
                    client.close()

            acknowledged: dict[str, Any] | None = None
            acknowledged_utc: str | None = None
            deadline = time.monotonic() + args.failpoint_timeout
            while time.monotonic() < deadline:
                control_text: str | None = None
                try:
                    polling_client = guest.connect()
                    try:
                        control_text = get_text_if_exists(polling_client, control_path)
                    finally:
                        polling_client.close()
                except Exception:
                    control_text = None
                if control_text:
                    acknowledged = json.loads(control_text)
                    acknowledged_utc = utc_now()
                    break
                if child_stdout is not None and child_stdout.channel.exit_status_ready():
                    child_exit = child_stdout.channel.recv_exit_status()
                    child_out = child_stdout.read().decode("utf-8", errors="replace")
                    child_err = child_stderr.read().decode("utf-8", errors="replace") if child_stderr else ""
                    raise RuntimeError(
                        f"Hard-reset child exited before its failpoint acknowledgement ({child_exit}). "
                        f"stdout={child_out!r}; stderr={child_err!r}"
                    )
                time.sleep(0.25)
            if acknowledged is None:
                raise TimeoutError(f"Trial {trial_id} did not acknowledge failpoint {failpoint} within the timeout.")
            if acknowledged.get("TrialId") != trial_id or acknowledged.get("Failpoint") != failpoint:
                raise RuntimeError(f"Control record does not match planned trial {trial_id}/{failpoint}.")

            vm_before = domain_state(args.libvirt_uri, args.domain)
            reset_command = ["reset", args.domain]
            reset_time = utc_now()
            reset = run_virsh(args.libvirt_uri, *reset_command)
            if child_session is not None:
                child_session.close()
            vm_immediate = domain_state(args.libvirt_uri, args.domain)
            log_event({"trial_id": trial_id, "event": "acknowledged_then_abrupt_reset",
                       "acknowledged_utc": acknowledged_utc, "control_record": acknowledged,
                       "command": ["virsh", "-c", args.libvirt_uri, *reset_command],
                       "reset_started_utc": reset_time, "reset_completed_utc": utc_now(),
                       "vm_state_before_reset": vm_before, "vm_state_immediately_after_reset": vm_immediate,
                       "stdout": reset.stdout.strip(), "stderr": reset.stderr.strip(),
                       "host_sync_called": False, "guest_freeze_called": False,
                       "graceful_shutdown_called": False})
            if vm_before != "running":
                raise RuntimeError(f"The clone was not running when hard reset was requested: {vm_before}.")

            recovery_started = time.monotonic()
            recovery_deadline = recovery_started + args.ssh_timeout
            recovered = wait_for_guest(guest, args.ssh_timeout, deadline=recovery_deadline)
            log_event({"trial_id": trial_id, "event": "recovery_ssh_ready",
                       "elapsed_seconds": round(time.monotonic() - recovery_started, 3),
                       "deadline_seconds": args.ssh_timeout})
            try:
                inspect_status, inspection, control_after, inspect_error = None, None, None, None
                inspection_started = time.monotonic()
                inspection_deadline = inspection_started + DEFAULT_GUEST_COMMAND_TIMEOUT_SECONDS
                inspection_data, control_data, inspect_error = recover_and_capture(
                    guest, recovered, config, trial_root / "recovered", deadline=inspection_deadline)
                control_after = control_data
                inspect_status = 0 if inspection_data and inspection_data.get("ContractPassed") else 1
                inspection = inspection_data
                vm_after = domain_state(args.libvirt_uri, args.domain)
                failpoint_reached = (acknowledged.get("TrialId") == trial_id and
                                     acknowledged.get("Failpoint") == failpoint)
                passed = bool(failpoint_reached and inspection and inspection.get("ContractPassed") and not inspect_error)
                error_parts = [item for item in (inspect_error,) if item]
                error = "; ".join(error_parts)
                if inspect_status:
                    error = (error + "; " if error else "") + "recovery_contract_failed"
                log_event({"trial_id": trial_id, "event": "recovery_inspected",
                           "vm_state_after_recovery": vm_after, "inspection_exit_status": inspect_status,
                           "elapsed_seconds_after_reset": round(time.monotonic() - recovery_started, 3),
                           "inspection_elapsed_seconds": round(time.monotonic() - inspection_started, 3),
                           "contract_passed": passed, "error": error})

                inspection = inspection or {}
                markers = inspection.get("CommitMarkers", [])
                recovery_row = (
                    trial_id, "hard_reset", candidate, representation, failpoint, item["trial"], -1,
                    str(bool(failpoint_reached)).lower(), inspection.get("SelectedGeneration", ""),
                    inspection.get("SelectedGenerationClass", "unknown"), inspection.get("DocumentCount", 0),
                    inspection.get("FixedLookupHits", 0), ";".join(inspection.get("FixedLookupIds", [])),
                    str(bool(inspection.get("ReferencedFilesReadable", False))).lower(),
                    str(bool(inspection.get("DeepValidationPassed", False))).lower(),
                    ";".join(inspection.get("LeftoverTemporaryFiles", [])),
                    json.dumps(markers, separators=(",", ":")), str(passed).lower(), error,
                    index_path, str((trial_root / "trial-controller.json").as_posix()),
                )
                csv_writer.writerow(recovery_row)
                rows_file.flush()
                output_rows.append(recovery_row)
                (trial_root / "trial-controller.json").write_text(json.dumps({
                    "trial_id": trial_id, "planned_failpoint": failpoint,
                    "control_record_before_reset": acknowledged,
                    "control_record_after_reset": json.loads(control_after) if control_after else None,
                    "reset_command": ["virsh", "-c", args.libvirt_uri, *reset_command],
                    "reset_started_utc": reset_time, "vm_state_before_reset": vm_before,
                    "vm_state_immediately_after_reset": vm_immediate,
                    "vm_state_after_recovery": vm_after, "contract_passed": passed, "error": error,
                }, indent=2) + "\n", encoding="utf-8")
            finally:
                recovered.close()
            print(f"{len(output_rows)}/{len(plan)} {trial_id}: {'passed' if recovery_row[17] == 'true' else 'failed'}", flush=True)

    if len(output_rows) != 84:
        raise RuntimeError(f"Hard-reset controller wrote {len(output_rows)} rows; expected 84.")
    print(f"Hard-reset trial table: {rows_path}")
    return 0 if all(row[17] == "true" for row in output_rows) else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"Hard-reset controller failed: {type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(1)
