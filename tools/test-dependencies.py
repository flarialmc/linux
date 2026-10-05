#!/usr/bin/env python3
"""Test installer consent with an isolated PATH and fake privilege/package tools."""
import os
import pathlib
import pty
import select
import shutil
import subprocess
import tempfile
import time

SOURCE = (pathlib.Path(__file__).resolve().parents[1] / "packaging/install.sh").read_text()
TOOLS = "uname tar gzip zstd openssl python3 bash setsid stty xdg-open xprop curl wget".split()


def run_case(distro, manager, expected, answer=None, failure=False, immutable=False):
    with tempfile.TemporaryDirectory(prefix="flarial-installer-test-") as temp:
        root = pathlib.Path(temp)
        binary = root / "bin"
        system = root / "system"
        binary.mkdir()
        system.mkdir()
        for tool in TOOLS:
            path = shutil.which(tool)
            assert path, f"test prerequisite missing: {tool}"
            (binary / tool).symlink_to(path)
        (root / "os-release").write_text(distro)
        marker = root / "installed"
        pkg = system / manager
        pkg.write_text('#!/bin/sh\nprintf "%s\\n" "$*" > "$TEST_MARKER"\n' +
                       ('exit 1\n' if failure else '/bin/ln -s /usr/bin/script "$TEST_BIN/script"\n'))
        pkg.chmod(0o755)
        sudo = system / "sudo"
        sudo.write_text('#!/bin/sh\n[ "$1" = -- ] && shift\nexec "$@"\n')
        sudo.chmod(0o755)
        immutable_marker = root / "ostree"
        if immutable:
            immutable_marker.touch()
        script = root / "install.sh"
        script.write_text(SOURCE.replace("/etc/os-release", str(root / "os-release"))
                          .replace("/usr/bin/", str(system) + "/")
                          .replace("/run/ostree-booted", str(immutable_marker))
                          .replace("/etc/NIXOS", str(root / "nix")))
        env = {**os.environ, "PATH": str(binary), "TEST_MARKER": str(marker), "TEST_BIN": str(binary)}
        if answer is None:
            result = subprocess.run(["/bin/sh", str(script), "--check-dependencies"], env=env,
                                    capture_output=True, text=True, start_new_session=True)
            code, output = result.returncode, result.stdout + result.stderr
        else:
            pid, fd = pty.fork()
            if pid == 0:
                os.execve("/bin/sh", ["sh", str(script), "--check-dependencies"], env)
            output = ""
            sent = False
            deadline = time.monotonic() + 10
            try:
                while time.monotonic() < deadline:
                    if select.select([fd], [], [], 0.1)[0]:
                        try:
                            data = os.read(fd, 65536)
                        except OSError:
                            break
                        if not data:
                            break
                        output += data.decode()
                        if "[y/N]" in output and not sent:
                            os.write(fd, (answer + "\n").encode())
                            sent = True
                else:
                    os.kill(pid, 9)
                    raise AssertionError("installer did not finish")
                _, status = os.waitpid(pid, 0)
                code = os.waitstatus_to_exitcode(status)
            finally:
                os.close(fd)
        assert ("rpm-ostree install util-linux-script" if immutable else expected) in output, output
        if answer == "y" and not immutable:
            assert marker.exists(), output
            assert marker.read_text().strip() == expected.removeprefix("sudo " + manager + " "), output
            assert code == (1 if failure else 0), output
            if not failure:
                assert (binary / "script").exists(), output
                assert "Linux dependencies are ready" in output, output
        else:
            assert code != 0 and not marker.exists(), output
        print("ok", distro.splitlines()[0], "answer=" + str(answer), "failure=" + str(failure), "immutable=" + str(immutable))


run_case("ID=fedora\nVERSION_ID=44\n", "dnf", "sudo dnf install -y util-linux-script")
run_case("ID=fedora\nVERSION_ID=44\n", "dnf", "sudo dnf install -y util-linux-script", "n")
run_case("ID=fedora\nVERSION_ID=44\n", "dnf", "sudo dnf install -y util-linux-script", "y")
run_case("ID=fedora\nVERSION_ID=44\n", "dnf", "sudo dnf install -y util-linux-script", "y", failure=True)
run_case("ID=fedora\nVERSION_ID=44\n", "dnf", "sudo dnf install -y util-linux-script", immutable=True)
run_case('ID=cachyos\nID_LIKE="arch"\n', "pacman", "sudo pacman -S --needed --noconfirm util-linux", "y")
run_case("ID=ubuntu\nID_LIKE=debian\n", "apt-get", "sudo apt-get install -y bsdutils", "y")
run_case('ID=opensuse-tumbleweed\nID_LIKE="suse opensuse"\n', "zypper", "sudo zypper --non-interactive install util-linux", "y")
