import importlib.util
import contextlib
import io
import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path


SIDECAR_PATH = Path(__file__).resolve().parents[1] / "sidecar.py"


def load_sidecar_module():
    spec = importlib.util.spec_from_file_location("easyget_douyin_sidecar", SIDECAR_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SidecarCliTests(unittest.TestCase):
    def run_sidecar(self, args, output_dir, env=None):
        command = [
            sys.executable,
            str(SIDECAR_PATH),
            "--url",
            "https://www.douyin.com/video/7604129988555574538",
            "--output-dir",
            str(output_dir),
            *args,
        ]
        return subprocess.run(command, text=True, capture_output=True, check=False, env=env)

    def test_emit_sample_outputs_parseable_json_lines(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--emit-sample"], Path(temp_dir))

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertGreaterEqual(len(events), 2)
            progress = events[0]
            summary = events[-1]
            self.assertEqual(progress["event"], "progress")
            self.assertEqual(progress["percent"], 10)
            self.assertIn("downloaded_bytes", progress)
            self.assertIn("total_bytes", progress)
            self.assertIn("speed_bytes_per_sec", progress)
            self.assertIn("eta_seconds", progress)
            self.assertEqual(summary["event"], "success")
            self.assertEqual(summary["title"], "EasyGet Douyin sidecar sample")
            self.assertEqual(summary["platform"], "douyin")
            self.assertEqual(summary["duration_seconds"], 0)
            self.assertIsNone(summary["thumbnail_url"])
            self.assertGreater(summary["file_size_bytes"], 0)
            self.assertTrue(summary["output_file_path"])
            self.assertTrue(Path(summary["output_file_path"]).exists())

    def test_dry_run_outputs_single_summary_with_planned_command(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run", "--mode", "post", "--limit", "3"], Path(temp_dir))

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(len(events), 1)
            summary = events[0]
            self.assertEqual(summary["event"], "log")
            self.assertEqual(summary["message"], "dry_run")
            self.assertIn("planned_command", summary["details"])
            self.assertEqual(summary["details"]["config"]["number"]["post"], 3)

    def test_dry_run_maps_multiple_user_modes_to_third_party_number_config(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--mode", "like,mix,music", "--limit", "5"],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertEqual(config["mode"], ["like", "mix", "music"])
            self.assertEqual(config["number"]["post"], 0)
            self.assertEqual(config["number"]["like"], 5)
            self.assertEqual(config["number"]["mix"], 5)
            self.assertEqual(config["number"]["music"], 5)
            self.assertEqual(config["number"]["allmix"], 5)

    def test_dry_run_maps_collect_modes_to_third_party_number_config(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--mode", "collect,collectmix", "--limit", "7"],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertEqual(config["mode"], ["collect", "collectmix"])
            self.assertEqual(config["number"]["post"], 0)
            self.assertEqual(config["number"]["collect"], 7)
            self.assertEqual(config["number"]["collectmix"], 7)

    def test_dry_run_accepts_csharp_runner_arguments_and_include_flags(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                [
                    "--dry-run",
                    "--format",
                    "mp4",
                    "--quality",
                    "1080",
                    "--title",
                    "sample title",
                    "--mode",
                    "post",
                    "--limit",
                    "3",
                    "--include-cover",
                    "--include-music",
                    "--include-json",
                    "--include-avatar",
                ],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertTrue(config["cover"])
            self.assertTrue(config["music"])
            self.assertTrue(config["json"])
            self.assertTrue(config["avatar"])
            self.assertEqual("1080p", config["video_quality"])

    def test_dry_run_maps_easyget_quality_to_third_party_video_quality(self):
        cases = [
            ("best", "highest"),
            ("2160", "highest"),
            ("1080", "1080p"),
            ("720", "720p"),
            ("480", "480p"),
            ("1080p", "1080p"),
            ("unknown", "highest"),
        ]

        for quality, expected in cases:
            with self.subTest(quality=quality), tempfile.TemporaryDirectory() as temp_dir:
                result = self.run_sidecar(["--dry-run", "--quality", quality], Path(temp_dir))

                self.assertEqual(result.returncode, 0, result.stderr)
                events = [json.loads(line) for line in result.stdout.splitlines()]
                config = events[0]["details"]["config"]
                self.assertEqual(expected, config["video_quality"])

    def test_dry_run_defaults_comment_collection_to_disabled(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run"], Path(temp_dir))

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertFalse(config["comments"]["enabled"])

    def test_dry_run_defaults_database_to_disabled_with_state_database_path(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            result = self.run_sidecar(["--dry-run"], output_dir)

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            expected_database_path = output_dir.resolve() / ".easyget-douyin-sidecar" / "dy_downloader.db"
            self.assertFalse(config["database"])
            self.assertEqual(config["database_path"], str(expected_database_path))
            self.assertEqual(
                config["increase"],
                {
                    "post": False,
                    "like": False,
                    "mix": False,
                    "music": False,
                    "allmix": False,
                },
            )

    def test_dry_run_enable_database_sets_local_database_path(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            result = self.run_sidecar(["--dry-run", "--enable-database"], output_dir)

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            expected_database_path = output_dir.resolve() / ".easyget-douyin-sidecar" / "dy_downloader.db"
            self.assertTrue(config["database"])
            self.assertEqual(config["database_path"], str(expected_database_path))

    def test_dry_run_incremental_like_mix_auto_enables_database_and_selected_increase(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            result = self.run_sidecar(
                ["--dry-run", "--incremental", "--mode", "like,mix", "--limit", "5"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            expected_database_path = output_dir.resolve() / ".easyget-douyin-sidecar" / "dy_downloader.db"
            self.assertTrue(config["database"])
            self.assertEqual(config["database_path"], str(expected_database_path))
            self.assertEqual(
                config["increase"],
                {
                    "post": False,
                    "like": True,
                    "mix": True,
                    "music": False,
                    "allmix": True,
                },
            )

    def test_dry_run_include_comments_enables_comment_collection(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run", "--include-comments"], Path(temp_dir))

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertTrue(config["comments"]["enabled"])

    def test_invalid_cookie_outputs_failed_event_with_compatible_error_fields(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run", "--cookie", "{not-json"], Path(temp_dir))

            self.assertEqual(result.returncode, 1)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(len(events), 1)
            failure = events[0]
            self.assertEqual(failure["event"], "failed")
            self.assertIn("cookie JSON is invalid", failure["error"])
            self.assertEqual(failure["message"], failure["error"])
            self.assertEqual(failure["reason"], failure["error"])

    def test_cookie_env_dry_run_redacts_cookie_and_reports_source(self):
        secret_cookie = "ttwid=env-secret-value; odin_tt=env-other-secret"
        env = {**os.environ, "EASYGET_TEST_DOUYIN_COOKIE": secret_cookie}
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--cookie-env", "EASYGET_TEST_DOUYIN_COOKIE"],
                Path(temp_dir),
                env=env,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertNotIn(secret_cookie, result.stdout)
            self.assertNotIn("env-secret-value", result.stdout)
            self.assertNotIn("env-other-secret", result.stdout)
            self.assertNotIn(secret_cookie, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            details = events[0]["details"]
            self.assertEqual(details["cookie_source"]["source"], "env")
            self.assertEqual(details["cookie_source"]["env"], "EASYGET_TEST_DOUYIN_COOKIE")
            self.assertTrue(details["cookie_source"]["present"])
            self.assertTrue(details["cookie_source"]["redacted"])
            self.assertEqual(details["config"]["cookies"]["ttwid"], "***")
            self.assertEqual(details["config"]["cookies"]["odin_tt"], "***")

    def test_cookie_file_dry_run_redacts_cookie_and_reports_source(self):
        secret_cookie = "ttwid=file-secret-value; odin_tt=file-other-secret"
        with tempfile.TemporaryDirectory() as temp_dir:
            base = Path(temp_dir)
            cookie_file = base / "cookie.txt"
            cookie_file.write_text(secret_cookie, encoding="utf-8")

            result = self.run_sidecar(
                ["--dry-run", "--cookie-file", str(cookie_file)],
                base / "downloads",
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertNotIn(secret_cookie, result.stdout)
            self.assertNotIn("file-secret-value", result.stdout)
            self.assertNotIn("file-other-secret", result.stdout)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            details = events[0]["details"]
            self.assertEqual(details["cookie_source"]["source"], "file")
            self.assertEqual(details["cookie_source"]["path"], str(cookie_file.resolve()))
            self.assertTrue(details["cookie_source"]["present"])
            self.assertTrue(details["cookie_source"]["redacted"])
            self.assertEqual(details["config"]["cookies"]["ttwid"], "***")
            self.assertEqual(details["config"]["cookies"]["odin_tt"], "***")

    def test_cookie_env_and_file_together_emit_failed_event(self):
        env = {**os.environ, "EASYGET_TEST_DOUYIN_COOKIE": "ttwid=must-not-leak"}
        with tempfile.TemporaryDirectory() as temp_dir:
            cookie_file = Path(temp_dir) / "cookie.txt"
            cookie_file.write_text("ttwid=file-must-not-leak", encoding="utf-8")

            result = self.run_sidecar(
                [
                    "--dry-run",
                    "--cookie-env",
                    "EASYGET_TEST_DOUYIN_COOKIE",
                    "--cookie-file",
                    str(cookie_file),
                ],
                Path(temp_dir) / "downloads",
                env=env,
            )

            self.assertEqual(result.returncode, 2)
            self.assertNotIn("must-not-leak", result.stdout)
            self.assertNotIn("file-must-not-leak", result.stdout)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            failure = events[0]
            self.assertEqual(failure["event"], "failed")
            self.assertIn("only one cookie source", failure["error"].lower())

    def test_missing_or_empty_cookie_sources_emit_failed_event(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            base = Path(temp_dir)
            empty_cookie_file = base / "empty-cookie.txt"
            empty_cookie_file.write_text(" \n\t", encoding="utf-8")
            cases = [
                ("missing env", ["--dry-run", "--cookie-env", "EASYGET_COOKIE_DOES_NOT_EXIST"], {}),
                ("empty env", ["--dry-run", "--cookie-env", "EASYGET_EMPTY_COOKIE"], {"EASYGET_EMPTY_COOKIE": "  "}),
                ("missing file", ["--dry-run", "--cookie-file", str(base / "missing-cookie.txt")], {}),
                ("empty file", ["--dry-run", "--cookie-file", str(empty_cookie_file)], {}),
            ]

            for name, args, extra_env in cases:
                with self.subTest(name=name):
                    env = {**os.environ, **extra_env}
                    result = self.run_sidecar(args, base / name.replace(" ", "-"), env=env)

                    self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                    self.assertEqual(result.stderr, "")
                    events = [json.loads(line) for line in result.stdout.splitlines()]
                    self.assertEqual(events[0]["event"], "failed")
                    self.assertIn("cookie", events[0]["error"].lower())

    def test_cookie_source_errors_are_validated_before_emit_sample(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--emit-sample", "--cookie-env", "EASYGET_COOKIE_DOES_NOT_EXIST"],
                Path(temp_dir),
                env={**os.environ},
            )

            self.assertEqual(result.returncode, 2)
            self.assertEqual(result.stderr, "")
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(events[0]["event"], "failed")
            self.assertIn("environment variable is not set", events[0]["error"])

    def test_self_test_imports_outputs_success_for_importable_downloader_root(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "out"
            make_fake_importable_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--self-test-imports"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(len(events), 1)
            summary = events[0]
            self.assertEqual(summary["event"], "success")
            self.assertEqual(summary["details"]["downloader_root"], str(root.resolve()))
            self.assertTrue(summary["details"]["imports_ok"])
            self.assertIn("core.api_client", summary["details"]["checked_modules"])

    def test_self_test_imports_outputs_failed_event_for_missing_modules(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "out"
            root.mkdir()
            (root / "run.py").write_text("", encoding="utf-8")

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--self-test-imports"],
                output_dir,
            )

            self.assertEqual(result.returncode, 2)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(events[-1]["event"], "failed")
            self.assertIn("import self-test failed", events[-1]["error"])
            self.assertFalse(events[-1]["details"]["imports_ok"])
            self.assertIn("config", events[-1]["details"]["failed_modules"])


def make_fake_downloader(
    root: Path,
    *,
    exit_code: int = 0,
    mutate_runtime: bool = False,
    produce_outputs: bool = True,
    manifest_missing_file: bool = False,
    count_success: int = 1,
    write_probe: bool = True,
    cookie_warning: bool = False,
    leak_cookie_from_config: bool = False,
) -> Path:
    run_py = root / "run.py"
    if manifest_missing_file:
        output_block = """
            media_path = output_dir / "missing-video.mp4"
            (output_dir / "download_manifest.jsonl").write_text(
                json.dumps({"file_paths": [str(media_path)], "desc": "missing manifest video"}) + "\\n",
                encoding="utf-8",
            )
    """
    else:
        output_block = """
            media_path = output_dir / "fake-video.mp4"
            media_path.write_bytes(b"fake media")
            (output_dir / "download_manifest.jsonl").write_text(
                json.dumps({"file_paths": [str(media_path)], "desc": "fake in-process video"}) + "\\n",
                encoding="utf-8",
            )
    """ if produce_outputs else ""
    probe_block = """
            (output_dir / "runtime-probe.json").write_text(
                json.dumps({
                    "cwd": os.getcwd(),
                    "argv": sys.argv,
                    "path0": sys.path[0],
                }),
                encoding="utf-8",
            )
    """ if write_probe else ""
    leak_cookie_block = """
            config_path = Path(sys.argv[sys.argv.index("-c") + 1])
            config_data = json.loads(config_path.read_text(encoding="utf-8"))
            print("COOKIE_LEAK_PROBE=" + "; ".join(
                f"{key}={value}" for key, value in config_data.get("cookies", {}).items()
            ))
    """ if leak_cookie_from_config else ""
    run_py.write_text(
        textwrap.dedent(
            f"""
            import json
            import os
            import sys
            from pathlib import Path

            print("THIRD_PARTY_STDOUT_SHOULD_BE_CAPTURED")
            print("THIRD_PARTY_STDERR_SHOULD_BE_CAPTURED", file=sys.stderr)
            if {str(bool(cookie_warning))}:
                print("⚠ Cookies may be invalid or incomplete")
{leak_cookie_block}

            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)
{output_block}
{probe_block}
            print("Total: 1, Success: {int(count_success)}, Failed: 0, Skipped: 0")

            if {str(bool(mutate_runtime))}:
                sys.argv[:] = ["mutated-by-fake-runner"]
                sys.path.insert(0, str(output_dir / "mutated-path"))
                os.chdir(output_dir)

            raise SystemExit({int(exit_code)})
            """
        ).lstrip(),
        encoding="utf-8",
    )
    return run_py


def make_fake_importable_downloader(root: Path) -> None:
    root.mkdir(parents=True)
    (root / "run.py").write_text("", encoding="utf-8")
    modules = [
        "config/__init__.py",
        "auth/__init__.py",
        "auth/cookie_manager.py",
        "core/__init__.py",
        "core/api_client.py",
        "core/url_parser.py",
        "storage/__init__.py",
        "storage/file_manager.py",
        "storage/database.py",
    ]
    for module_path in modules:
        path = root / module_path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("VALUE = 'ok'\n", encoding="utf-8")


class SidecarRunnerTests(unittest.TestCase):
    def run_sidecar(self, args, output_dir, env=None):
        command = [
            sys.executable,
            str(SIDECAR_PATH),
            "--url",
            "https://www.douyin.com/video/7604129988555574538",
            "--output-dir",
            str(output_dir),
            *args,
        ]
        return subprocess.run(command, text=True, capture_output=True, check=False, env=env)

    def test_in_process_runner_executes_fake_downloader_and_keeps_stdout_jsonl(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertNotIn("THIRD_PARTY_STDOUT_SHOULD_BE_CAPTURED", result.stdout)
            self.assertNotIn("THIRD_PARTY_STDERR_SHOULD_BE_CAPTURED", result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual([event["event"] for event in events], ["progress", "success"])
            self.assertEqual(events[-1]["title"], "fake in-process video")
            self.assertTrue((output_dir / "runtime-probe.json").exists())

    def test_run_real_default_temp_config_stays_outside_output_directory(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            probe = json.loads((output_dir / "runtime-probe.json").read_text(encoding="utf-8"))
            config_index = probe["argv"].index("-c")
            config_path = Path(probe["argv"][config_index + 1]).resolve()
            with self.assertRaises(ValueError):
                config_path.relative_to(output_dir.resolve())
            self.assertFalse(
                any(path.name.startswith(".easyget-douyin-sidecar-") for path in output_dir.iterdir()),
                "Default runtime config directories should not be created inside the user's output directory.",
            )

    def test_in_process_runner_converts_nonzero_system_exit_to_failed_event(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(root, exit_code=7)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 7)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(events[-1]["event"], "failed")
            self.assertEqual(events[-1]["details"]["return_code"], 7)
            self.assertEqual(events[-1]["details"]["counts"]["failed"], 1)
            self.assertEqual(result.stderr, "")

    def test_in_process_runner_restores_process_runtime_state(self):
        sidecar = load_sidecar_module()
        original_cwd = Path.cwd()
        original_argv = sys.argv[:]
        original_path = sys.path[:]

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            run_py = make_fake_downloader(root, mutate_runtime=True)

            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                exit_code = sidecar.main(
                    [
                        "--url",
                        "https://www.douyin.com/video/7604129988555574538",
                        "--output-dir",
                        str(output_dir),
                        "--downloader-root",
                        str(root),
                        "--runner-mode",
                        "in-process",
                    ]
                )

            self.assertEqual(exit_code, 0, stdout.getvalue())
            self.assertEqual(Path.cwd(), original_cwd)
            self.assertEqual(sys.argv, original_argv)
            self.assertEqual(sys.path, original_path)
            probe = json.loads((output_dir / "runtime-probe.json").read_text(encoding="utf-8"))
            self.assertEqual(Path(probe["cwd"]).resolve(), root.resolve())
            self.assertEqual(Path(probe["argv"][0]).resolve(), run_py.resolve())
            self.assertIn("-c", probe["argv"])
            self.assertIn("-p", probe["argv"])
            self.assertEqual(Path(probe["path0"]).resolve(), root.resolve())

    def test_run_real_exit_zero_without_output_files_emits_failed_event(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(root, produce_outputs=False, count_success=1, write_probe=False)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 1)
            self.assertEqual(result.stderr, "")
            for line in result.stdout.splitlines():
                json.loads(line)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(events[-1]["event"], "failed")
            self.assertIn("no output files", events[-1]["error"])
            self.assertIn("THIRD_PARTY_STDOUT_SHOULD_BE_CAPTURED", events[-1]["error"])
            self.assertIn("THIRD_PARTY_STDERR_SHOULD_BE_CAPTURED", events[-1]["error"])
            self.assertEqual(events[-1]["details"]["counts"]["success"], 1)

    def test_run_real_probe_json_is_not_treated_as_download_output(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(root, produce_outputs=False, count_success=1, write_probe=True)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 1)
            self.assertEqual(result.stderr, "")
            events = [json.loads(line) for line in result.stdout.splitlines()]
            failure = events[-1]
            self.assertEqual(failure["event"], "failed")
            self.assertIn("no output files", failure["error"])
            self.assertEqual(failure["details"]["output_files"], [])

    def test_run_real_exit_zero_with_manifest_missing_file_emits_failed_event(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(root, manifest_missing_file=True, count_success=1, write_probe=False)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 1)
            self.assertEqual(result.stderr, "")
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(events[-1]["event"], "failed")
            self.assertIn("no output files", events[-1]["error"])
            self.assertEqual(events[-1]["details"]["output_files"], [])

    def test_run_real_exit_zero_with_zero_success_count_emits_failed_event(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(root, produce_outputs=True, count_success=0)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 1)
            self.assertEqual(result.stderr, "")
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(events[-1]["event"], "failed")
            self.assertEqual(events[-1]["details"]["counts"]["success"], 0)
            self.assertIn("THIRD_PARTY_STDOUT_SHOULD_BE_CAPTURED", events[-1]["error"])

    def test_run_real_cookie_warning_without_outputs_emits_clear_failed_event(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(
                root,
                produce_outputs=False,
                count_success=0,
                write_probe=False,
                cookie_warning=True,
            )

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 1)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            failure = events[-1]
            self.assertEqual(failure["event"], "failed")
            self.assertIn("Cookie", failure["error"])
            self.assertIn("invalid or incomplete", failure["error"])
            self.assertNotIn("THIRD_PARTY_STDOUT_SHOULD_BE_CAPTURED", failure["error"])
            self.assertIn("captured_output_tail", failure["details"])

    def test_run_real_redacts_cookie_if_third_party_output_leaks_config(self):
        secret_cookie = "ttwid=real-secret-value; odin_tt=real-other-secret"
        env = {**os.environ, "EASYGET_TEST_DOUYIN_COOKIE": secret_cookie}
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(
                root,
                produce_outputs=False,
                count_success=1,
                write_probe=False,
                leak_cookie_from_config=True,
            )

            result = self.run_sidecar(
                [
                    "--downloader-root",
                    str(root),
                    "--runner-mode",
                    "in-process",
                    "--cookie-env",
                    "EASYGET_TEST_DOUYIN_COOKIE",
                ],
                output_dir,
                env=env,
            )

            self.assertEqual(result.returncode, 1)
            self.assertEqual(result.stderr, "")
            self.assertNotIn(secret_cookie, result.stdout)
            self.assertNotIn("real-secret-value", result.stdout)
            self.assertNotIn("real-other-secret", result.stdout)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            failure = events[-1]
            self.assertEqual(failure["event"], "failed")
            self.assertIn("COOKIE_LEAK_PROBE=ttwid=***", failure["error"])
            self.assertIn("odin_tt=***", failure["error"])


class SidecarConfigTests(unittest.TestCase):
    def test_build_config_defaults_optional_media_outputs_to_false(self):
        sidecar = load_sidecar_module()

        config = sidecar.build_config(
            url="https://www.douyin.com/video/7604129988555574538",
            output_dir=Path("F:/Downloads/Douyin"),
        )

        self.assertFalse(config["cover"])
        self.assertFalse(config["music"])
        self.assertFalse(config["json"])
        self.assertFalse(config["avatar"])

    def test_build_config_maps_include_flags(self):
        sidecar = load_sidecar_module()

        config = sidecar.build_config(
            url="https://www.douyin.com/video/7604129988555574538",
            output_dir=Path("F:/Downloads/Douyin"),
            include_cover=True,
            include_music=False,
            include_json=True,
            include_avatar=True,
        )

        self.assertTrue(config["cover"])
        self.assertFalse(config["music"])
        self.assertTrue(config["json"])
        self.assertTrue(config["avatar"])

    def test_resolve_downloader_python_prefers_third_party_venv_when_not_explicit(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            venv_python = root / ".venv" / "Scripts" / "python.exe"
            venv_python.parent.mkdir(parents=True)
            venv_python.write_text("", encoding="utf-8")

            resolved = sidecar.resolve_downloader_python("", root)

            self.assertEqual(str(venv_python.resolve()), resolved)

    def test_resolve_downloader_python_keeps_explicit_python(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            venv_python = root / ".venv" / "Scripts" / "python.exe"
            venv_python.parent.mkdir(parents=True)
            venv_python.write_text("", encoding="utf-8")

            resolved = sidecar.resolve_downloader_python("C:/Python314/python.exe", root)

            self.assertEqual("C:/Python314/python.exe", resolved)

    def test_build_config_maps_cli_arguments_for_third_party_runner(self):
        sidecar = load_sidecar_module()
        output_dir = Path("F:/Downloads/Douyin")

        config = sidecar.build_config(
            url="https://www.douyin.com/user/MS4wLjABAAAA_test",
            output_dir=output_dir,
            cookie="ttwid=abc; odin_tt=def; passport_csrf_token=ghi",
            proxy="http://127.0.0.1:7890",
            mode="post",
            limit=12,
            quality="720",
        )

        self.assertEqual(config["link"], ["https://www.douyin.com/user/MS4wLjABAAAA_test"])
        self.assertEqual(config["path"], str(output_dir))
        self.assertEqual(config["mode"], ["post"])
        self.assertEqual(config["number"]["post"], 12)
        self.assertEqual(config["proxy"], "http://127.0.0.1:7890")
        self.assertEqual(config["cookies"]["ttwid"], "abc")
        self.assertEqual(config["cookies"]["odin_tt"], "def")
        self.assertEqual(config["cookies"]["passport_csrf_token"], "ghi")
        self.assertEqual(config["video_quality"], "720p")
        self.assertFalse(config["database"])
        self.assertFalse(config["browser_fallback"]["enabled"])

    def test_write_events_escapes_non_ascii_for_legacy_console_encoding(self):
        sidecar = load_sidecar_module()
        output = io.BytesIO()
        legacy_stdout = io.TextIOWrapper(output, encoding="gbk", newline="\n")

        with contextlib.redirect_stdout(legacy_stdout):
            sidecar.write_events([sidecar.make_failed_event("⚠ 登录态失效")], "jsonl")
            legacy_stdout.flush()

        payload = output.getvalue().decode("gbk")
        self.assertIn("\\u26a0", payload)
        event = json.loads(payload)
        self.assertEqual(event["event"], "failed")
        self.assertEqual(event["error"], "⚠ 登录态失效")

    def test_manifest_output_files_drops_paths_outside_output_dir(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            base = Path(temp_dir)
            output_dir = base / "downloads"
            outside_dir = base / "outside"
            output_dir.mkdir()
            outside_dir.mkdir()
            inside_file = output_dir / "inside.mp4"
            outside_absolute = outside_dir / "absolute.mp4"
            escaped_relative = output_dir / ".." / "escaped.mp4"
            inside_file.write_bytes(b"inside")
            manifest = output_dir / "download_manifest.jsonl"
            manifest.write_text(
                json.dumps(
                    {
                        "file_paths": [
                            str(inside_file),
                            str(outside_absolute),
                            "../escaped.mp4",
                        ],
                    }
                )
                + "\n",
                encoding="utf-8",
            )

            output_files, entries = sidecar.manifest_output_files(output_dir, 0)

            self.assertEqual(output_files, [str(inside_file.resolve())])
            self.assertEqual(len(entries), 1)
            self.assertNotIn(str(outside_absolute.resolve()), output_files)
            self.assertNotIn(str(escaped_relative.resolve()), output_files)

    def test_manifest_output_files_drops_missing_files_inside_output_dir(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            existing_file = output_dir / "existing.mp4"
            missing_file = output_dir / "missing.mp4"
            existing_file.write_bytes(b"existing")
            manifest = output_dir / "download_manifest.jsonl"
            manifest.write_text(
                json.dumps(
                    {
                        "file_paths": [
                            str(existing_file),
                            str(missing_file),
                        ],
                    }
                )
                + "\n",
                encoding="utf-8",
            )

            output_files, entries = sidecar.manifest_output_files(output_dir, 0)

            self.assertEqual(output_files, [str(existing_file.resolve())])
            self.assertEqual(len(entries), 1)
            self.assertNotIn(str(missing_file.resolve()), output_files)

    def test_manifest_output_files_ignores_sidecar_state_directory_files(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            downloaded_file = output_dir / "video.mp4"
            state_dir = output_dir / ".easyget-douyin-sidecar-test"
            state_file = state_dir / "config.json"
            downloaded_file.write_bytes(b"video")
            state_dir.mkdir()
            state_file.write_text('{"cookies":{"ttwid":"secret"}}', encoding="utf-8")
            manifest = output_dir / "download_manifest.jsonl"
            manifest.write_text(
                json.dumps(
                    {
                        "file_paths": [
                            str(downloaded_file),
                            str(state_file),
                        ],
                    }
                )
                + "\n",
                encoding="utf-8",
            )

            output_files, entries = sidecar.manifest_output_files(output_dir, 0)

            self.assertEqual(output_files, [str(downloaded_file.resolve())])
            self.assertEqual(len(entries), 1)
            self.assertNotIn(str(state_file.resolve()), output_files)

    def test_manifest_output_files_ignores_unrecognized_json_and_txt_files(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            video_file = output_dir / "video.mp4"
            data_file = output_dir / "video_data.json"
            comments_file = output_dir / "video_comments.txt"
            probe_file = output_dir / "runtime-probe.json"
            notes_file = output_dir / "notes.txt"
            for path in (video_file, data_file, comments_file, probe_file, notes_file):
                path.write_bytes(b"x")
            manifest = output_dir / "download_manifest.jsonl"
            manifest.write_text(
                json.dumps(
                    {
                        "file_paths": [
                            str(video_file),
                            str(data_file),
                            str(comments_file),
                            str(probe_file),
                            str(notes_file),
                        ],
                    }
                )
                + "\n",
                encoding="utf-8",
            )

            output_files, entries = sidecar.manifest_output_files(output_dir, 0)

            self.assertEqual(
                set(output_files),
                {
                    str(video_file.resolve()),
                    str(data_file.resolve()),
                    str(comments_file.resolve()),
                },
            )
            self.assertEqual(len(entries), 1)
            self.assertNotIn(str(probe_file.resolve()), output_files)
            self.assertNotIn(str(notes_file.resolve()), output_files)

    def test_scan_recent_output_files_ignores_unrecognized_json_and_txt_files(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            video_file = output_dir / "video.mp4"
            data_file = output_dir / "video_data.json"
            comments_file = output_dir / "video_comments.txt"
            probe_file = output_dir / "runtime-probe.json"
            notes_file = output_dir / "notes.txt"
            for path in (video_file, data_file, comments_file, probe_file, notes_file):
                path.write_bytes(b"x")

            output_files = sidecar.scan_recent_output_files(output_dir, 0)

            self.assertEqual(
                set(output_files),
                {
                    str(video_file.resolve()),
                    str(data_file.resolve()),
                    str(comments_file.resolve()),
                },
            )
            self.assertNotIn(str(probe_file.resolve()), output_files)
            self.assertNotIn(str(notes_file.resolve()), output_files)


if __name__ == "__main__":
    unittest.main()
