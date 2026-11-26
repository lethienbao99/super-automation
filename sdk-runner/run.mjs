// Chạy một phiên Claude qua Claude Agent SDK thay cho `claude -p`, giữ nguyên giao diện mà ClaudeCli.cs dùng:
//   node run.mjs <các tham số giống claude -p>   (prompt qua stdin)
// và in mỗi message ra stdout dạng JSON một dòng, cùng định dạng `--output-format stream-json`.
// Dùng khi xác thực bằng API key mà credit chỉ áp cho Agent SDK/API (không áp cho Claude Code).
// Key đọc từ biến SA_ANTHROPIC_KEY, không nằm trên dòng lệnh.
import { query } from '@anthropic-ai/claude-agent-sdk';

const argv = process.argv.slice(2);
const opt = {
  // Giống Claude Code CLI: system prompt của Claude Code + CLAUDE.md, skill, agent, settings của user/project
  systemPrompt: { type: 'preset', preset: 'claude_code' },
  settingSources: ['user', 'project', 'local'],
  cwd: process.cwd(),
  additionalDirectories: [],
};
const list = s => s.split(',').map(x => x.trim()).filter(Boolean);
for (let i = 0; i < argv.length; i++) {
  const a = argv[i], v = () => argv[++i];
  switch (a) {
    case '-p': case '--print': case '--verbose': break;
    case '--output-format': v(); break;
    case '--permission-mode': opt.permissionMode = v(); break;
    case '--model': opt.model = v(); break;
    case '--allowedTools': opt.allowedTools = list(v()); break;
    case '--disallowedTools': opt.disallowedTools = list(v()); break;
    case '--add-dir': opt.additionalDirectories.push(v()); break;
    case '--resume': opt.resume = v(); break;
    case '--max-turns': opt.maxTurns = Number(v()); break;
    case '--max-budget-usd': opt.maxBudgetUsd = Number(v()); break;
    case '--settings': v(); break; // chỉ dùng cho apiKeyHelper của chế độ CLI
    default: process.stderr.write(`sdk-runner: bỏ qua tham số không hỗ trợ ${a}\n`);
  }
}
if (opt.permissionMode === 'bypassPermissions') opt.allowDangerouslySkipPermissions = true;

const env = { ...process.env, ANTHROPIC_API_KEY: process.env.SA_ANTHROPIC_KEY || '' };
// Biến thừa hưởng từ phiên Claude Code/VSCode đã khởi động tool có thể làm SDK nhận sai môi trường
for (const k of Object.keys(env)) if (k.startsWith('CLAUDE_CODE_') || k === 'CLAUDECODE') delete env[k];
delete env.SA_ANTHROPIC_KEY;
delete env.ANTHROPIC_AUTH_TOKEN;
opt.env = env;
opt.stderr = s => process.stderr.write(s);

let prompt = '';
process.stdin.setEncoding('utf8');
for await (const chunk of process.stdin) prompt += chunk;

const out = m => process.stdout.write(JSON.stringify(m) + '\n');
let gotResult = false;
try {
  for await (const m of query({ prompt, options: opt })) {
    if (m.type === 'result') gotResult = true;
    out(m);
  }
} catch (e) {
  if (!gotResult) out({ type: 'result', subtype: 'error_during_execution', is_error: true, result: `Agent SDK lỗi: ${e?.message || e}`, total_cost_usd: 0 });
  process.exitCode = 1;
}
