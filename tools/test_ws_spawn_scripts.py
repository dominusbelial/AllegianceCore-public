import json
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).parent / 'allegiance-mcp/scripts/bots-spawn-perpetual.sh'


class WsSpawnScriptsTests(unittest.TestCase):
    def run_script(self, arguments, extra_env=None):
        with tempfile.TemporaryDirectory() as directory:
            stub = Path(directory) / 'dotnet-stub'
            stub.write_text('#!/usr/bin/env python3\nimport json, sys\nprint(json.dumps(sys.argv[1:]))\n')
            stub.chmod(0o755)
            env = {k: v for k, v in os.environ.items() if not k.startswith('ALLEGIANCE_BOT_')}
            env.update(DOTNET=str(stub))
            env.update(extra_env or {})
            return subprocess.run(['bash', str(SCRIPT), *arguments], env=env, text=True, capture_output=True)

    def test_forwards_all_options_without_shell_interpretation(self):
        options = ['--transport', 'ws', '--ws-url', 'wss://load.example.invalid/game',
                   '--ws-origin', 'https://load.example.invalid', '--ws-ca-file', '/tmp/CA space.pem',
                   '--ws-fraction', '0.5']
        result = self.run_script(['load.example.invalid', '6073', '2.0.0', '--seats', '50',
                                  '--i-mean-it-host', 'load.example.invalid', *options])
        self.assertEqual(0, result.returncode, result.stderr)
        args = json.loads(result.stdout)
        self.assertEqual(['load.example.invalid', '6073', '--backfill-seats', '50', '--client-version', '2.0.0', *options], args[1:])

    def test_remote_tcp_ws_and_environment_targets_require_exact_acknowledgement(self):
        for arguments, env in [(['192.0.2.1'], {}),
                               (['127.0.0.1', '--ws-url', 'wss://load.example.invalid/game'], {}),
                               (['127.0.0.1'], {'ALLEGIANCE_BOT_WS_URL': 'wss://load.example.invalid/game'}),
                               (['192.0.2.1', '--i-mean-it-host', '192.0.2.2'], {})]:
            result = self.run_script(arguments, env)
            self.assertNotEqual(0, result.returncode)
            self.assertIn('Refusing non-loopback', result.stderr)
            self.assertEqual('', result.stdout)

    def test_experiment_uses_exact_proxy_and_private_ws_listener(self):
        root = Path(__file__).resolve().parents[1]
        spec = importlib.util.spec_from_file_location('deploy_check', root / 'tools/p1061-deploy-check.py')
        parser = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(parser)
        base = parser.parse_yaml(root / 'docker-compose.yml')
        experiment = parser.parse_yaml(root / 'docker/rig/compose.websocket-experiment.yml')
        config = json.loads((root / 'docker/rig/server.websocket.example.json').read_text())
        proxy = experiment['services']['ws-proxy']
        address = proxy['networks']['ws-experiment']['ipv4_address']
        self.assertEqual([address], config['webSocket']['knownProxies'])
        self.assertEqual((128, 256), (config['webSocket']['maxConnectionsPerIp'], config['webSocket']['handshakeAttemptsPerIpPerWindow']))
        self.assertFalse(config['webSocket']['allowLoopbackDevelopment'])
        self.assertFalse(any('6080' in port for port in experiment['services']['server']['ports']))
        self.assertIn('ALLEGIANCE_BENCHMARK_METRICS', base['services']['server']['environment'])
        caddy = (root / 'docker/rig/Caddyfile.websocket').read_text()
        self.assertIn('tls internal', caddy)
        self.assertIn('header_up X-Forwarded-For {remote_host}', caddy)
        self.assertIn('header_up X-Forwarded-Proto https', caddy)

    def test_loopback_defaults_and_missing_values(self):
        result = self.run_script(['127.0.0.1'])
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(['127.0.0.1', '6073', '--backfill-seats', '5'], json.loads(result.stdout)[1:])
        self.assertNotEqual(0, self.run_script(['127.0.0.1', '--transport']).returncode)


if __name__ == '__main__':
    unittest.main()
