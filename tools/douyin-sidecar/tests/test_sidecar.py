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
from unittest import mock
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

    def run_sidecar_without_url(self, args, output_dir, env=None):
        command = [
            sys.executable,
            str(SIDECAR_PATH),
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
            config = summary["details"]["config"]
            self.assertEqual(config["number"]["post"], 3)
            self.assertEqual(config["thread"], 3)
            self.assertEqual(config["author_dir"], "nickname")
            self.assertIs(config["group_by_mode"], True)

    def test_dry_run_hot_board_outputs_discovery_plan_without_url(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar_without_url(["--dry-run", "--hot-board", "30"], Path(temp_dir))

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(len(events), 1)
            summary = events[0]
            self.assertEqual(summary["event"], "log")
            self.assertEqual(summary["message"], "dry_run")
            discovery = summary["details"]["discovery"]
            self.assertEqual(discovery["type"], "hot_board")
            self.assertEqual(discovery["limit"], 30)
            self.assertIn("--hot-board", summary["details"]["planned_command"])
            self.assertNotIn("--url", summary["details"]["planned_command"])

    def test_dry_run_search_outputs_discovery_plan_without_url(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar_without_url(
                ["--dry-run", "--search", "猫咪", "--search-max", "2"],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            summary = events[0]
            discovery = summary["details"]["discovery"]
            self.assertEqual(discovery["type"], "search")
            self.assertEqual(discovery["keyword"], "猫咪")
            self.assertEqual(discovery["search_max"], 2)
            self.assertIn("--search", summary["details"]["planned_command"])
            self.assertIn("--search-max", summary["details"]["planned_command"])

    def test_dry_run_hot_board_without_limit_plans_unlimited_flag_value(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar_without_url(["--dry-run", "--hot-board"], Path(temp_dir))

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            summary = events[0]
            discovery = summary["details"]["discovery"]
            self.assertEqual(discovery["type"], "hot_board")
            self.assertEqual(discovery["limit"], 0)
            planned_command = summary["details"]["planned_command"]
            hot_board_index = planned_command.index("--hot-board")
            self.assertEqual(planned_command[hot_board_index + 1], "0")

    def test_url_is_required_for_non_discovery_commands(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar_without_url(["--dry-run"], Path(temp_dir))

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("--url is required unless --hot-board or --search is used", result.stderr)

    def test_discovery_arguments_are_validated(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            cases = [
                (["--dry-run", "--hot-board", "-1"], "--hot-board must be >= 0"),
                (["--dry-run", "--search", ""], "--search keyword cannot be empty"),
                (["--dry-run", "--search", "猫咪", "--search-max", "0"], "--search-max must be >= 1"),
                (
                    ["--dry-run", "--hot-board", "10", "--search", "猫咪"],
                    "--hot-board and --search cannot be used together",
                ),
            ]

            for args, expected_error in cases:
                with self.subTest(args=args):
                    result = self.run_sidecar_without_url(args, output_dir)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn(expected_error, result.stderr)

    def test_dry_run_uses_thread_count(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run", "--thread", "7"], Path(temp_dir))

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertEqual(config["thread"], 7)

    def test_dry_run_rejects_invalid_thread_count(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run", "--thread", "0"], Path(temp_dir))

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("--thread must be >= 1", result.stderr)

    def test_dry_run_uses_custom_filename_and_folder_templates(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                [
                    "--dry-run",
                    "--filename-template",
                    "{author}_{title}_{id}",
                    "--folder-template",
                    "{date}_{id}",
                ],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertEqual(config["filename_template"], "{author}_{title}_{id}")
            self.assertEqual(config["folder_template"], "{date}_{id}")

    def test_dry_run_defaults_blank_templates(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                [
                    "--dry-run",
                    "--filename-template",
                    " \t ",
                    "--folder-template",
                    "",
                ],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertEqual(config["filename_template"], "{date}_{title}_{id}")
            self.assertEqual(config["folder_template"], "{date}_{title}_{id}")

    def test_dry_run_uses_author_directory_and_mode_grouping_options(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                [
                    "--dry-run",
                    "--author-dir",
                    "sec_uid",
                    "--no-group-by-mode",
                ],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertEqual(config["author_dir"], "sec_uid")
            self.assertIs(config["group_by_mode"], False)

    def test_dry_run_defaults_unknown_author_directory_mode(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--author-dir", "unknown"],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertEqual(config["author_dir"], "nickname")

    def test_dry_run_enables_browser_fallback_for_profile_pagination(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run", "--browser-fallback"], Path(temp_dir))

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertTrue(config["browser_fallback"]["enabled"])
            self.assertFalse(config["browser_fallback"]["headless"])

    def test_dry_run_maps_live_recording_options(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                [
                    "--dry-run",
                    "--url",
                    "https://live.douyin.com/123456789",
                    "--live-max-duration-seconds",
                    "3600",
                    "--live-chunk-size",
                    "131072",
                    "--live-idle-timeout-seconds",
                    "45",
                ],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertEqual(
                {
                    "max_duration_seconds": 3600,
                    "chunk_size": 131072,
                    "idle_timeout_seconds": 45,
                },
                config["live"],
            )

    def test_dry_run_rejects_unknown_template_variable(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--filename-template", "{date}_{nickname}_{id}"],
                Path(temp_dir),
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stderr, "")
            failure = json.loads(result.stdout)
            self.assertEqual(failure["event"], "failed")
            self.assertIn("--filename-template", failure["error"])
            self.assertIn("unknown variable", failure["error"])
            self.assertNotIn("{date}_{nickname}_{id}", result.stdout)

    def test_dry_run_rejects_template_path_separators(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--folder-template", "{author}/{id}"],
                Path(temp_dir),
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stderr, "")
            failure = json.loads(result.stdout)
            self.assertEqual(failure["event"], "failed")
            self.assertIn("--folder-template", failure["error"])
            self.assertIn("unsafe character", failure["error"])
            self.assertNotIn("{author}/{id}", result.stdout)

    def test_dry_run_rejects_unicode_control_character_template(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--filename-template", "{date}_{title}_{id}\u0085part"],
                Path(temp_dir),
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stderr, "")
            failure = json.loads(result.stdout)
            self.assertEqual(failure["event"], "failed")
            self.assertIn("--filename-template", failure["error"])
            self.assertIn("control character", failure["error"])
            self.assertNotIn("part", result.stdout)

    def test_dry_run_rejects_template_missing_id(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--filename-template", "{date}_{title}"],
                Path(temp_dir),
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stderr, "")
            failure = json.loads(result.stdout)
            self.assertEqual(failure["event"], "failed")
            self.assertIn("--filename-template", failure["error"])
            self.assertIn("{id}", failure["error"])
            self.assertNotIn("{date}_{title}", result.stdout)

    def test_dry_run_rejects_overlong_template(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--filename-template", f"{{id}}_{'x' * 201}"],
                Path(temp_dir),
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stderr, "")
            failure = json.loads(result.stdout)
            self.assertEqual(failure["event"], "failed")
            self.assertIn("--filename-template", failure["error"])
            self.assertIn("200", failure["error"])
            self.assertNotIn("x" * 201, result.stdout)

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

    def test_dry_run_maps_filter_arguments_to_third_party_config(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                [
                    "--dry-run",
                    "--start-time",
                    " 2024-02-29 ",
                    "--end-time",
                    "2024-03-01",
                    "--download-pinned",
                ],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertEqual(config["start_time"], "2024-02-29")
            self.assertEqual(config["end_time"], "2024-03-01")
            self.assertTrue(config["download_pinned"])

    def test_dry_run_rejects_nonexistent_start_date(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--start-time", "2024-02-30"],
                Path(temp_dir),
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stderr, "")
            events = [json.loads(line) for line in result.stdout.splitlines()]
            failure = events[0]
            self.assertEqual(failure["event"], "failed")
            self.assertIn("--start-time", failure["error"])
            self.assertIn("YYYY-MM-DD", failure["error"])

    def test_dry_run_rejects_end_date_with_slashes(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--end-time", "2024/03/01"],
                Path(temp_dir),
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stderr, "")
            events = [json.loads(line) for line in result.stdout.splitlines()]
            failure = events[0]
            self.assertEqual(failure["event"], "failed")
            self.assertIn("--end-time", failure["error"])
            self.assertIn("YYYY-MM-DD", failure["error"])

    def test_dry_run_omits_blank_filter_arguments_from_third_party_config(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                [
                    "--dry-run",
                    "--start-time",
                    " \t ",
                    "--end-time",
                    "",
                ],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertNotIn("start_time", config)
            self.assertNotIn("end_time", config)
            self.assertNotIn("download_pinned", config)

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
            self.assertFalse(config["comments"]["include_replies"])
            self.assertEqual(config["comments"]["max_comments"], 0)
            self.assertEqual(config["comments"]["page_size"], 20)

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
            result = self.run_sidecar(
                [
                    "--dry-run",
                    "--include-comments",
                    "--comment-include-replies",
                    "--max-comments",
                    "500",
                    "--comment-page-size",
                    "12",
                ],
                Path(temp_dir),
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            config = events[0]["details"]["config"]
            self.assertTrue(config["comments"]["enabled"])
            self.assertTrue(config["comments"]["include_replies"])
            self.assertEqual(config["comments"]["max_comments"], 500)
            self.assertEqual(config["comments"]["page_size"], 12)

    def test_dry_run_rejects_invalid_comment_limits(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run", "--max-comments", "-1"], Path(temp_dir))
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("--max-comments must be >= 0", result.stderr)

            result = self.run_sidecar(["--dry-run", "--comment-page-size", "21"], Path(temp_dir))
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("--comment-page-size must be between 1 and 20", result.stderr)

    def test_raw_cookie_argument_is_rejected_without_leaking_secret(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run", "--cookie", "sessionid=secret"], Path(temp_dir))

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stderr, "")
            self.assertNotIn("sessionid=secret", result.stdout)
            self.assertNotIn("sessionid=secret", result.stderr)
            self.assertNotIn("secret", result.stdout)
            self.assertNotIn("secret", result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual(len(events), 1)
            failure = events[0]
            self.assertEqual(failure["event"], "failed")
            self.assertIn("Raw Cookie command-line input is not supported", failure["error"])
            self.assertEqual(failure["message"], failure["error"])
            self.assertEqual(failure["reason"], failure["error"])
            self.assertNotIn("sessionid=secret", json.dumps(failure, ensure_ascii=False))
            self.assertNotIn("secret", json.dumps(failure, ensure_ascii=False))

    def assert_raw_cookie_rejection_does_not_leak(self, result, *secrets):
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stderr, "")
        for secret in secrets:
            self.assertNotIn(secret, result.stdout)
            self.assertNotIn(secret, result.stderr)
        failure = json.loads(result.stdout)
        self.assertEqual(failure["event"], "failed")
        self.assertIn("Raw Cookie command-line input is not supported", failure["error"])
        payload = json.dumps(failure, ensure_ascii=False)
        for secret in secrets:
            self.assertNotIn(secret, payload)

    def test_raw_cookie_argument_with_extra_token_is_rejected_before_argparse_without_leaking_secret(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                ["--dry-run", "--cookie", "sessionid=SENTINEL", "odin_tt=SECOND_SENTINEL"],
                Path(temp_dir),
            )

            self.assert_raw_cookie_rejection_does_not_leak(result, "SENTINEL", "SECOND_SENTINEL")

    def test_raw_cookie_equals_argument_is_rejected_without_leaking_secret(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--dry-run", "--cookie=sessionid=SENTINEL"], Path(temp_dir))

            self.assert_raw_cookie_rejection_does_not_leak(result, "SENTINEL")

    def test_raw_cookie_argument_with_json_output_format_is_rejected_without_leaking_secret(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(
                [
                    "--output-format",
                    "json",
                    "--dry-run",
                    "--cookie",
                    "sessionid=SENTINEL",
                    "extra=SECOND_SENTINEL",
                ],
                Path(temp_dir),
            )

            self.assert_raw_cookie_rejection_does_not_leak(result, "SENTINEL", "SECOND_SENTINEL")

    def test_help_hides_raw_cookie_argument_but_keeps_secure_cookie_sources(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = self.run_sidecar(["--help"], Path(temp_dir))

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stderr, "")
            self.assertNotRegex(result.stdout, r"(?m)^\s*--cookie(?:\s|,|$)")
            self.assertIn("--cookie-env", result.stdout)
            self.assertIn("--cookie-file", result.stdout)

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
            self.assertTrue(summary["details"]["python_executable"])
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

    def test_format_failure_output_summarizes_login_required_traceback(self):
        sidecar = load_sidecar_module()

        message = sidecar.format_failure_output(
            """
            Traceback (most recent call last):
              File "cli/main.py", line 438, in main
                asyncio.run(main_async(args))
            core.api_client.LoginRequiredError: login required (status_code=2483)
            at /aweme/v1/web/general/search/single/: 请先登录，再继续搜索吧
            [ERROR] Playwright is not installed.
            """
        )

        self.assertIn("Cookie", message)
        self.assertIn("登录态", message)
        self.assertIn("请先登录", message)
        self.assertNotIn("Traceback", message)


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
                    "pythonioencoding": os.environ.get("PYTHONIOENCODING"),
                    "pythonutf8": os.environ.get("PYTHONUTF8"),
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


def make_manifest_summary_downloader(root: Path) -> Path:
    run_py = root / "run.py"
    run_py.write_text(
        textwrap.dedent(
            """
            import json
            import sys
            from pathlib import Path

            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)

            video_path = output_dir / "video.mp4"
            gallery_path = output_dir / "gallery.jpg"
            music_path = output_dir / "music.mp3"
            for path in (video_path, gallery_path, music_path):
                path.write_bytes(b"media")

            manifest = output_dir / "download_manifest.jsonl"
            manifest.write_text(
                "\\n".join(
                    [
                        "",
                        "{malformed json",
                        json.dumps(["not", "an", "object"], ensure_ascii=False),
                        json.dumps(
                            {
                                "aweme_id": "video-1",
                                "media_type": "video",
                                "date": "2026-07-01",
                                "publish_timestamp": 1782892800,
                                "recorded_at": "2026-07-01T12:00:00Z",
                                "author_name": "Alice",
                                "desc": "video desc",
                                "tags": ["tag-a", "tag-b"],
                                "file_paths": [str(video_path)],
                                "file_names": [video_path.name],
                            },
                            ensure_ascii=False,
                        ),
                        json.dumps(
                            {
                                "aweme_id": "gallery-1",
                                "media_type": "gallery",
                                "date": "2026-07-02",
                                "author_name": "Bob",
                                "desc": "gallery desc",
                                "file_paths": [str(gallery_path)],
                                "file_names": [gallery_path.name],
                            },
                            ensure_ascii=False,
                        ),
                        json.dumps(
                            {
                                "aweme_id": "music-1",
                                "media_type": "music",
                                "date": "2026-07-03",
                                "author_name": "Carol",
                                "desc": "music desc without optional fields",
                                "file_paths": [str(music_path)],
                                "file_names": [music_path.name],
                            },
                            ensure_ascii=False,
                        ),
                    ]
                )
                + "\\n",
                encoding="utf-8",
            )
            print("Total: 3, Success: 3, Failed: 0, Skipped: 0")
            """
        ).lstrip(),
        encoding="utf-8",
    )
    return run_py


def make_append_manifest_downloader(root: Path) -> Path:
    run_py = root / "run.py"
    run_py.write_text(
        textwrap.dedent(
            """
            import json
            import sys
            from pathlib import Path

            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)

            video_path = output_dir / "new-video.mp4"
            gallery_path = output_dir / "new-gallery.jpg"
            for path in (video_path, gallery_path):
                path.write_bytes(b"media")

            manifest = output_dir / "download_manifest.jsonl"
            with manifest.open("a", encoding="utf-8") as handle:
                handle.write(
                    json.dumps(
                        {
                            "aweme_id": "new-video",
                            "media_type": "video",
                            "desc": "new video",
                            "file_paths": [str(video_path)],
                            "file_names": [video_path.name],
                        },
                        ensure_ascii=False,
                    )
                    + "\\n"
                )
                handle.write(
                    json.dumps(
                        {
                            "aweme_id": "new-gallery",
                            "media_type": "gallery",
                            "desc": "new gallery",
                            "file_paths": [str(gallery_path)],
                            "file_names": [gallery_path.name],
                        },
                        ensure_ascii=False,
                    )
                    + "\\n"
                )
            print("Total: 2, Success: 2, Failed: 0, Skipped: 0")
            """
        ).lstrip(),
        encoding="utf-8",
    )
    return run_py


def make_author_summary_downloader(root: Path) -> Path:
    run_py = root / "run.py"
    run_py.write_text(
        textwrap.dedent(
            """
            import json
            import sys
            from pathlib import Path

            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)

            manifest_entries = []
            for index, author_name in enumerate(("Alice", "Bob", "Alice"), start=1):
                media_path = output_dir / f"author-video-{index}.mp4"
                media_path.write_bytes(b"media")
                manifest_entries.append(
                    {
                        "aweme_id": f"author-video-{index}",
                        "media_type": "video",
                        "author_name": author_name,
                        "desc": f"{author_name} video {index}",
                        "file_paths": [str(media_path)],
                        "file_names": [media_path.name],
                    }
                )

            manifest = output_dir / "download_manifest.jsonl"
            manifest.write_text(
                "".join(json.dumps(entry, ensure_ascii=False) + "\\n" for entry in manifest_entries),
                encoding="utf-8",
            )
            print("Total: 3, Success: 3, Failed: 0, Skipped: 0")
            """
        ).lstrip(),
        encoding="utf-8",
    )
    return run_py


def make_file_only_downloader(root: Path, *, produce_outputs: bool = True, count_success: int = 1) -> Path:
    run_py = root / "run.py"
    output_block = """
            media_path = output_dir / "file-only-video.mp4"
            media_path.write_bytes(b"media")
    """ if produce_outputs else ""
    run_py.write_text(
        textwrap.dedent(
            f"""
            import sys
            from pathlib import Path

            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)
{output_block}
            print("Total: 1, Success: {int(count_success)}, Failed: 0, Skipped: 0")
            """
        ).lstrip(),
        encoding="utf-8",
    )
    return run_py


def make_database_status_downloader(root: Path) -> Path:
    run_py = root / "run.py"
    run_py.write_text(
        textwrap.dedent(
            """
            import json
            import sys
            from pathlib import Path

            config_path = Path(sys.argv[sys.argv.index("-c") + 1])
            config = json.loads(config_path.read_text(encoding="utf-8"))
            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)

            database_path = Path(config["database_path"])
            database_path.parent.mkdir(parents=True, exist_ok=True)
            database_path.write_bytes(b"sqlite placeholder")

            media_path = output_dir / "database-video.mp4"
            media_path.write_bytes(b"media")
            (output_dir / "download_manifest.jsonl").write_text(
                json.dumps({"file_paths": [str(media_path)], "desc": "database video"}) + "\\n",
                encoding="utf-8",
            )
            print("Total: 1, Success: 1, Failed: 0, Skipped: 0")
            """
        ).lstrip(),
        encoding="utf-8",
    )
    return run_py


def make_transcript_outputs_downloader(root: Path) -> Path:
    run_py = root / "run.py"
    run_py.write_text(
        textwrap.dedent(
            """
            import json
            import sys
            from pathlib import Path

            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)

            video_path = output_dir / "demo.mp4"
            text_path = output_dir / "demo.transcript.txt"
            json_path = output_dir / "demo.transcript.json"
            srt_path = output_dir / "demo.transcript.srt"
            for path in (video_path, text_path, json_path, srt_path):
                path.write_bytes(b"media")

            manifest = output_dir / "download_manifest.jsonl"
            manifest.write_text(
                json.dumps(
                    {
                        "aweme_id": "demo",
                        "media_type": "video",
                        "desc": "demo with transcript",
                        "file_paths": [str(video_path), str(text_path), str(json_path), str(srt_path)],
                        "file_names": [video_path.name, text_path.name, json_path.name, srt_path.name],
                    },
                    ensure_ascii=False,
                )
                + "\\n",
                encoding="utf-8",
            )
            print("Total: 1, Success: 1, Failed: 0, Skipped: 0")
            """
        ).lstrip(),
        encoding="utf-8",
    )
    return run_py


def make_live_room_metadata_downloader(root: Path) -> Path:
    run_py = root / "run.py"
    run_py.write_text(
        textwrap.dedent(
            """
            import json
            import sys
            from pathlib import Path

            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)

            media_path = output_dir / "live_recording.flv"
            room_path = output_dir / "live_recording_room.json"
            media_path.write_bytes(b"live media")
            room_path.write_text(
                json.dumps(
                    {
                        "room": {
                            "title": "测试直播间标题",
                            "status": 2,
                        },
                        "user": {
                            "nickname": "主播甲",
                        },
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            print("Total: 1, Success: 1, Failed: 0, Skipped: 0")
            """
        ).lstrip(),
        encoding="utf-8",
    )
    return run_py


def make_discovery_downloader(root: Path) -> Path:
    run_py = root / "run.py"
    run_py.write_text(
        textwrap.dedent(
            """
            import json
            import sys
            from pathlib import Path

            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)
            (output_dir / "discovery-probe.json").write_text(
                json.dumps({"argv": sys.argv}, ensure_ascii=False),
                encoding="utf-8",
            )

            if "--hot-board" in sys.argv:
                output_path = output_dir / "hot_board" / "20260703_120000.jsonl"
                output_path.parent.mkdir(parents=True, exist_ok=True)
                output_path.write_text(
                    "\\n".join(
                        [
                            json.dumps({"word": "猫咪", "hot_value": 123}, ensure_ascii=False),
                            json.dumps({"word": "旅行", "hot_value": 45}, ensure_ascii=False),
                        ]
                    )
                    + "\\n",
                    encoding="utf-8",
                )
                print("hot board exported")
                raise SystemExit(0)

            if "--search" in sys.argv:
                output_path = output_dir / "search" / "猫咪_20260703_120000.jsonl"
                output_path.parent.mkdir(parents=True, exist_ok=True)
                output_path.write_text(
                    "\\n".join(
                        [
                            json.dumps(
                                {
                                    "aweme_id": "aweme-1",
                                    "desc": "猫咪晒太阳",
                                    "author": {"nickname": "Alice", "sec_uid": "sec-a"},
                                },
                                ensure_ascii=False,
                            ),
                            json.dumps(
                                {
                                    "aweme_id": "aweme-2",
                                    "desc": "猫咪打盹",
                                    "author_user_info": {"nickname": "Bob", "sec_uid": "sec-b"},
                                },
                                ensure_ascii=False,
                            ),
                        ]
                    )
                    + "\\n",
                    encoding="utf-8",
                )
                print("search exported")
                raise SystemExit(0)

            print("missing discovery command", file=sys.stderr)
            raise SystemExit(2)
            """
        ).lstrip(),
        encoding="utf-8",
    )
    return run_py


def make_broken_discovery_downloader(root: Path, *, behavior: str) -> Path:
    run_py = root / "run.py"
    if behavior == "exit":
        body = """
            import sys
            print("discovery failed intentionally", file=sys.stderr)
            raise SystemExit(7)
        """
    elif behavior == "no_output":
        body = """
            import sys
            from pathlib import Path
            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_dir.mkdir(parents=True, exist_ok=True)
            print("no discovery output")
            raise SystemExit(0)
        """
    elif behavior == "bad_jsonl":
        body = """
            import sys
            from pathlib import Path
            output_dir = Path(sys.argv[sys.argv.index("-p") + 1])
            output_path = output_dir / "hot_board" / "bad.jsonl"
            output_path.parent.mkdir(parents=True, exist_ok=True)
            output_path.write_text("{bad json\\n", encoding="utf-8")
            raise SystemExit(0)
        """
    else:
        raise ValueError(behavior)

    run_py.write_text(textwrap.dedent(body).lstrip(), encoding="utf-8")
    return run_py


def read_jsonl(path: Path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def assert_no_file_paths_field(test_case: unittest.TestCase, value):
    if isinstance(value, dict):
        test_case.assertNotIn("file_paths", value)
        for child in value.values():
            assert_no_file_paths_field(test_case, child)
    elif isinstance(value, list):
        for child in value:
            assert_no_file_paths_field(test_case, child)


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
    def test_auto_runner_mode_defaults_to_subprocess_for_dependency_isolation(self):
        sidecar = load_sidecar_module()
        args = type("Args", (), {"runner_mode": "auto", "python": "", "timeout": 0})()

        self.assertEqual(sidecar.select_runner_mode(args), "subprocess")

    def test_explicit_in_process_runner_mode_is_preserved(self):
        sidecar = load_sidecar_module()
        args = type("Args", (), {"runner_mode": "in-process", "python": "", "timeout": 0})()

        self.assertEqual(sidecar.select_runner_mode(args), "in-process")

    def test_auto_subprocess_runner_sets_utf8_environment(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_fake_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root)],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            probe = json.loads((output_dir / "runtime-probe.json").read_text(encoding="utf-8"))
            self.assertEqual(probe["pythonioencoding"], "utf-8")
            self.assertEqual(probe["pythonutf8"], "1")

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

    def run_sidecar_without_url(self, args, output_dir, env=None):
        command = [
            sys.executable,
            str(SIDECAR_PATH),
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

    def test_live_room_metadata_sets_success_title(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_live_room_metadata_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            success = events[-1]
            room_path = output_dir / "live_recording_room.json"

            self.assertEqual(success["event"], "success")
            self.assertEqual(success["title"], "测试直播间标题")
            self.assertEqual(success["output_file_path"], str((output_dir / "live_recording.flv").resolve()))
            self.assertIn(str(room_path.resolve()), success["details"]["output_files"])
            self.assertEqual(
                success["details"]["live_room"],
                {
                    "title": "测试直播间标题",
                    "author_name": "主播甲",
                    "status": 2,
                    "status_text": "直播中",
                },
            )

    def test_hot_board_invokes_downloader_and_emits_discovery_event(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_discovery_downloader(root)

            result = self.run_sidecar_without_url(
                ["--downloader-root", str(root), "--runner-mode", "in-process", "--hot-board", "2"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual([event["event"] for event in events], ["progress", "success"])
            summary = events[-1]
            details = summary["details"]
            self.assertEqual(summary["title"], "EasyGet Douyin hot board discovery")
            self.assertEqual(details["kind"], "discovery")
            self.assertEqual(details["discovery_type"], "hot_board")
            self.assertEqual(details["limit"], 2)
            self.assertEqual(details["item_count"], 2)
            self.assertEqual(details["items"][0]["word"], "猫咪")
            self.assertNotIn("raw", details["items"][0])
            self.assertTrue(Path(details["output_file_path"]).exists())
            probe = json.loads((output_dir / "discovery-probe.json").read_text(encoding="utf-8"))
            self.assertIn("--hot-board", probe["argv"])
            self.assertIn("2", probe["argv"])

    def test_search_invokes_downloader_and_emits_discovery_event_as_json(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_discovery_downloader(root)

            result = self.run_sidecar_without_url(
                [
                    "--downloader-root",
                    str(root),
                    "--runner-mode",
                    "in-process",
                    "--search",
                    "猫咪",
                    "--search-max",
                    "2",
                    "--output-format",
                    "json",
                ],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(len(result.stdout.splitlines()), 1)
            summary = json.loads(result.stdout)
            details = summary["details"]
            self.assertEqual(summary["event"], "success")
            self.assertEqual(summary["title"], "EasyGet Douyin search discovery")
            self.assertEqual(details["discovery_type"], "search")
            self.assertEqual(details["kind"], "discovery")
            self.assertEqual(details["keyword"], "猫咪")
            self.assertEqual(details["search_max"], 2)
            self.assertEqual(details["item_count"], 2)
            self.assertEqual(details["items"][0]["aweme_id"], "aweme-1")
            self.assertEqual(details["items"][0]["author_nickname"], "Alice")
            self.assertEqual(details["items"][1]["sec_uid"], "sec-b")
            self.assertNotIn("raw", details["items"][0])
            self.assertTrue(Path(details["output_file_path"]).exists())
            probe = json.loads((output_dir / "discovery-probe.json").read_text(encoding="utf-8"))
            self.assertIn("--search", probe["argv"])
            self.assertIn("--search-max", probe["argv"])

    def test_discovery_failure_paths_emit_failed_event(self):
        cases = [
            ("exit", "discovery_failed"),
            ("no_output", "discovery_no_output"),
            ("bad_jsonl", "discovery_read_output"),
        ]
        for behavior, expected_step in cases:
            with self.subTest(behavior=behavior), tempfile.TemporaryDirectory() as temp_dir:
                root = Path(temp_dir) / "fake-downloader"
                output_dir = Path(temp_dir) / "downloads"
                root.mkdir()
                make_broken_discovery_downloader(root, behavior=behavior)

                result = self.run_sidecar_without_url(
                    ["--downloader-root", str(root), "--runner-mode", "in-process", "--hot-board", "2"],
                    output_dir,
                )

                self.assertNotEqual(result.returncode, 0)
                events = [json.loads(line) for line in result.stdout.splitlines()]
                failure = events[-1]
                self.assertEqual(failure["event"], "failed")
                self.assertEqual(failure["details"]["step"], expected_step)
                self.assertEqual(failure["details"]["discovery"]["type"], "hot_board")

    def test_run_real_success_details_include_manifest_summary(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_manifest_summary_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            success = events[-1]
            details = success["details"]
            cumulative_manifest_path = output_dir / "download_manifest.jsonl"
            manifest_path = Path(details["manifest_path"])

            self.assertEqual(success["event"], "success")
            self.assertNotEqual(manifest_path.resolve(), cumulative_manifest_path.resolve())
            self.assertEqual(manifest_path.parent.resolve(), output_dir.resolve())
            self.assertTrue(manifest_path.name.startswith("download_manifest.easyget-"))
            self.assertEqual(manifest_path.suffix, ".jsonl")
            self.assertEqual(len(read_jsonl(manifest_path)), 3)
            self.assertEqual(details["manifest_item_count"], 3)
            self.assertEqual(details["media_type_counts"], {"video": 1, "gallery": 1, "music": 1})
            self.assertEqual(len(details["manifest_items"]), 3)
            self.assertEqual(details["manifest_items"][0]["aweme_id"], "video-1")
            self.assertEqual(details["manifest_items"][0]["file_count"], 1)
            self.assertEqual(details["manifest_items"][0]["file_names"], ["video.mp4"])
            self.assertEqual(details["manifest_items"][2]["media_type"], "music")

    def test_run_real_manifest_path_points_to_this_run_snapshot_only(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            output_dir.mkdir()
            old_media = output_dir / "old-video.mp4"
            old_media.write_bytes(b"old")
            cumulative_manifest_path = output_dir / "download_manifest.jsonl"
            cumulative_manifest_path.write_text(
                json.dumps(
                    {
                        "aweme_id": "old-video",
                        "media_type": "video",
                        "desc": "old video",
                        "file_paths": [str(old_media)],
                        "file_names": [old_media.name],
                    },
                    ensure_ascii=False,
                )
                + "\n",
                encoding="utf-8",
            )
            make_append_manifest_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            success = events[-1]
            details = success["details"]
            snapshot_path = Path(details["manifest_path"])

            self.assertEqual(success["event"], "success")
            self.assertNotEqual(snapshot_path.resolve(), cumulative_manifest_path.resolve())
            self.assertEqual(snapshot_path.parent.resolve(), output_dir.resolve())
            self.assertTrue(snapshot_path.name.startswith("download_manifest.easyget-"))
            self.assertEqual(snapshot_path.suffix, ".jsonl")
            snapshot_entries = read_jsonl(snapshot_path)
            self.assertEqual([entry["aweme_id"] for entry in snapshot_entries], ["new-video", "new-gallery"])
            self.assertNotIn("old-video", [entry["aweme_id"] for entry in snapshot_entries])
            self.assertEqual(details["manifest_item_count"], 2)
            self.assertEqual(details["media_type_counts"], {"video": 1, "gallery": 1})
            self.assertEqual([item["aweme_id"] for item in details["manifest_items"]], ["new-video", "new-gallery"])

    def test_run_real_manifest_items_omit_raw_file_paths(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_manifest_summary_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            manifest_items = events[-1]["details"]["manifest_items"]

            self.assertTrue(manifest_items)
            for item in manifest_items:
                self.assertNotIn("file_paths", item)
                self.assertLessEqual(
                    set(item),
                    {
                        "aweme_id",
                        "media_type",
                        "date",
                        "publish_timestamp",
                        "recorded_at",
                        "author_name",
                        "desc",
                        "tags",
                        "file_count",
                        "file_names",
                    },
                )

    def test_run_real_manifest_summary_details_omit_raw_file_paths(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_manifest_summary_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]

            assert_no_file_paths_field(self, events[-1]["details"])

    def test_run_real_success_details_include_author_summaries(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_author_summary_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            details = events[-1]["details"]

            self.assertEqual(events[-1]["event"], "success")
            self.assertEqual(
                details["author_summaries"],
                [
                    {"author_name": "Alice", "work_count": 2},
                    {"author_name": "Bob", "work_count": 1},
                ],
            )
            assert_no_file_paths_field(self, details)

    def test_run_real_success_details_count_transcript_outputs(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_transcript_outputs_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            details = events[-1]["details"]

            self.assertEqual(events[-1]["event"], "success")
            self.assertEqual(details["transcript_file_count"], 3)
            self.assertEqual(
                [Path(path).name for path in details["transcript_files"]],
                ["demo.transcript.txt", "demo.transcript.json", "demo.transcript.srt"],
            )

    def test_run_real_success_details_include_database_status(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            make_database_status_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process", "--enable-database"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            details = events[-1]["details"]
            database_path = output_dir.resolve() / ".easyget-douyin-sidecar" / "dy_downloader.db"

            self.assertEqual(events[-1]["event"], "success")
            self.assertEqual(
                details["database"],
                {
                    "enabled": True,
                    "path": str(database_path),
                    "exists": True,
                },
            )
            self.assertNotIn(str(database_path), details["output_files"])

    def test_run_real_success_without_new_manifest_entry_does_not_attach_stale_manifest_path(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            output_dir.mkdir()
            old_media = output_dir / "old-video.mp4"
            old_media.write_bytes(b"old")
            os.utime(old_media, (946684800, 946684800))
            (output_dir / "download_manifest.jsonl").write_text(
                json.dumps(
                    {
                        "aweme_id": "old-video",
                        "media_type": "video",
                        "desc": "old video",
                        "file_paths": [str(old_media)],
                        "file_names": [old_media.name],
                    },
                    ensure_ascii=False,
                )
                + "\n",
                encoding="utf-8",
            )
            make_file_only_downloader(root)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            details = events[-1]["details"]

            self.assertEqual(events[-1]["event"], "success")
            self.assertEqual(details["manifest_path"], "")
            self.assertEqual(details["manifest_item_count"], 0)
            self.assertEqual(details["media_type_counts"], {})
            self.assertEqual(details["manifest_items"], [])

    def test_run_real_failure_without_new_manifest_entry_does_not_attach_stale_manifest_path(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir) / "fake-downloader"
            output_dir = Path(temp_dir) / "downloads"
            root.mkdir()
            output_dir.mkdir()
            old_media = output_dir / "old-video.mp4"
            old_media.write_bytes(b"old")
            os.utime(old_media, (946684800, 946684800))
            (output_dir / "download_manifest.jsonl").write_text(
                json.dumps(
                    {
                        "aweme_id": "old-video",
                        "media_type": "video",
                        "desc": "old video",
                        "file_paths": [str(old_media)],
                        "file_names": [old_media.name],
                    },
                    ensure_ascii=False,
                )
                + "\n",
                encoding="utf-8",
            )
            make_file_only_downloader(root, produce_outputs=False, count_success=1)

            result = self.run_sidecar(
                ["--downloader-root", str(root), "--runner-mode", "in-process"],
                output_dir,
            )

            self.assertEqual(result.returncode, 1)
            events = [json.loads(line) for line in result.stdout.splitlines()]
            failure = events[-1]

            self.assertEqual(failure["event"], "failed")
            self.assertIn("no output files", failure["error"])
            self.assertNotIn("manifest_path", failure["details"])
            self.assertEqual(failure["details"]["output_files"], [])

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
            room_file = output_dir / "live_room.json"
            probe_file = output_dir / "runtime-probe.json"
            notes_file = output_dir / "notes.txt"
            for path in (video_file, data_file, comments_file, room_file, probe_file, notes_file):
                path.write_bytes(b"x")
            manifest = output_dir / "download_manifest.jsonl"
            manifest.write_text(
                json.dumps(
                    {
                        "file_paths": [
                            str(video_file),
                            str(data_file),
                            str(comments_file),
                            str(room_file),
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
                    str(room_file.resolve()),
                },
            )
            self.assertEqual(len(entries), 1)
            self.assertNotIn(str(probe_file.resolve()), output_files)
            self.assertNotIn(str(notes_file.resolve()), output_files)

    def test_manifest_output_files_streams_from_start_line_and_skips_malformed_non_object_lines(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            old_file = output_dir / "old.mp4"
            new_file = output_dir / "new.mp4"
            old_file.write_bytes(b"old")
            new_file.write_bytes(b"new")
            manifest = output_dir / "download_manifest.jsonl"
            with manifest.open("w", encoding="utf-8") as handle:
                handle.write(json.dumps({"aweme_id": "old", "file_paths": [str(old_file)]}) + "\n")
                handle.write("\n")
                handle.write("{bad json\n")
                handle.write(json.dumps(["not", "an", "object"]) + "\n")
                handle.write(json.dumps({"aweme_id": "new", "file_paths": [str(new_file)]}) + "\n")

            with mock.patch.object(Path, "read_text", side_effect=AssertionError("read_text should not be used")):
                output_files, entries = sidecar.manifest_output_files(output_dir, 1)

            self.assertEqual(output_files, [str(new_file.resolve())])
            self.assertEqual([entry["aweme_id"] for entry in entries], ["new"])

    def test_manifest_line_count_streams_manifest_file(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            manifest = output_dir / "download_manifest.jsonl"
            with manifest.open("w", encoding="utf-8") as handle:
                for index in range(250):
                    handle.write(json.dumps({"index": index}) + "\n")

            with mock.patch.object(Path, "read_text", side_effect=AssertionError("read_text should not be used")):
                count = sidecar.manifest_line_count(output_dir)

            self.assertEqual(count, 250)

    def test_scan_recent_output_files_ignores_unrecognized_json_and_txt_files(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            video_file = output_dir / "video.mp4"
            data_file = output_dir / "video_data.json"
            comments_file = output_dir / "video_comments.txt"
            room_file = output_dir / "live_room.json"
            probe_file = output_dir / "runtime-probe.json"
            notes_file = output_dir / "notes.txt"
            for path in (video_file, data_file, comments_file, room_file, probe_file, notes_file):
                path.write_bytes(b"x")

            output_files = sidecar.scan_recent_output_files(output_dir, 0)

            self.assertEqual(
                set(output_files),
                {
                    str(video_file.resolve()),
                    str(data_file.resolve()),
                    str(comments_file.resolve()),
                    str(room_file.resolve()),
                },
            )
            self.assertNotIn(str(probe_file.resolve()), output_files)
            self.assertNotIn(str(notes_file.resolve()), output_files)

    def test_load_first_metadata_file_prefers_aweme_data_over_live_room_snapshot(self):
        sidecar = load_sidecar_module()
        with tempfile.TemporaryDirectory() as temp_dir:
            output_dir = Path(temp_dir)
            room_file = output_dir / "live_room.json"
            data_file = output_dir / "video_data.json"
            room_file.write_text(
                json.dumps({"room": {"title": "直播标题"}, "user": {"nickname": "主播甲"}}, ensure_ascii=False),
                encoding="utf-8",
            )
            data_file.write_text(
                json.dumps({"desc": "作品标题"}, ensure_ascii=False),
                encoding="utf-8",
            )

            metadata = sidecar.load_first_metadata_file([str(room_file), str(data_file)])

            self.assertEqual(metadata["desc"], "作品标题")


if __name__ == "__main__":
    unittest.main()
