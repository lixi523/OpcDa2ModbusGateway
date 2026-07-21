# AGENTS.md - 项目开发规范

本规范优先**稳妥严谨**，而非追求开发速度；若只是简单琐碎的任务，可灵活酌情放宽。

---

## 1. 先思考，后编码

**不要假设。不要隐藏困惑。呈现权衡方案。**

在编写代码之前：

- 明确你的假设。如果不确定，提出疑问。

- 如果存在多种可能的解读，将它们列出——不要随意选定一种。

- 如果存在更简单的方案，要提出。在必要时可以反驳。

- 如果有任何不清楚的地方，停下来。明确指出困惑所在。向用户开口询问。

---

## 2. 简洁至上

**用最少的代码解决问题。不写任何推测性内容。**

- 不添加任何未被要求的功能。

- 不因为只有一次调用就引入抽象层。

- 如果未被要求，不要加入灵活性或可配置性。

- 不为不可能发生的场景编写错误处理。

- 如果你写了 200 行而实际上 50 行就能搞定，那就重写。

自我提问：资深工程师看了会评价这有点过度复杂吗？如果答案是肯定的，那就简化。

---

## 3. 精准修改

**只触碰必须改动的部分。只清理你自己造成的烂摊子。**

在编辑已有代码时：

- 不要顺手优化相邻的代码、注释或格式。

- 不要重构没出问题的部分。

- 遵循现有代码风格，即使你自己更常用另一种写法。

- 如果发现与当前任务无关的死代码，可以提出来——但不要删除它。

当你的改动制造了孤立的冗余部分时：

- 删除那些因为你的改动才变得无用的导入、变量或函数。

- 不要删除任何原本就存在的死代码（除非明确要求）。

检验标准：每一行被改动的代码，都应能直接追溯到用户的原始需求。

---

## 4. 目标计划执行

**定义成功标准。循环直至验证通过。**

将任务转化为可验证的目标：

- 添加验证 -> 先为无效输入编写测试，然后让测试通过

- 修复这个 Bug -> 先写一个能复现该问题的测试，然后使其通过

- 重构 X -> 确保重构前后所有现有测试均保持通过

对于多步骤的任务，先制定简要计划：

1. [步骤] -> 验证：[检查]
2. [步骤] -> 验证：[检查]
3. [步骤] -> 验证：[检查]

清晰明确的成功标准能让你在验证循环中自主推进。而模糊的标准（例如让它能跑就行）则需要你来来回回不断澄清。

---

## 5. Skill Evolution 规则

本规则参考 Hermes Agent 的 Background Review 机制实现。

### 5.1 自动触发条件

当完成涉及 >=5 步工具调用的复杂任务后，主动判断该流程是否可复用：

1. 自我检查四问（任务完成后立即）：
   - 本次任务是否使用了 >=5 个工具调用，且它们构成了一个连贯的目标序列？
   - 相同的输入/输出形式会在未来的会话中重复出现吗？
   - 是否已有现有技能覆盖了此场景？（执行去重检查）
   - 技能化的收益（未来节省的 token/时间）是否超过加载成本？

2. 六项可复用性判断标准：

| # | 标准 | 通过信号 | 失败信号 |
|---|------|----------|----------|
| 1 | 步骤数量 | >=5 个工具调用构成连贯序列 | 1-4 步，或零散无关的调用 |
| 2 | 非一次性 | 相同 I/O 形状未来会重现 | 本质上绑定到一个文件/仓库/时刻，不会重复 |
| 3 | 清晰的 I/O 形状 | 一句话能说明触发措辞和期望输出 | 输出取决于无法泛化的实时状态读取 |
| 4 | 可泛化 | 过程适用于一类输入，非单个实例 | 硬编码了无法迁移的路径/名称/值 |
| 5 | 超出常识 | 捕获了模型不会可靠再现的领域知识/工具序列 | 任何称职的模型默认就能做到的事 |
| 6 | 物有所值 | 技能在未来使用中节省的 token/时间 > 加载成本 | 收益微薄；描述很少匹配真实提示词 |

如果 1+2+3(no)+4 都通过，则执行沉淀；否则记录原因并继续。

### 5.2 沉淀格式

创建新技能时的目录结构：

~/.agents/skills/<skill-name>/
- SKILL.md              # 必需 - 主说明文件，<500 行
- references/           # （可选）详细信息，模型按需读取
- scripts/              # （可选）辅助脚本
- assets/               # （可选）模板、fixture

**SKILL.md frontmatter 要求**：
- name: 小写 kebab-case，1-40 字符，必须与目录名一致
- description: 第三人称，描述 WHAT + WHEN，<=1024 字符，包含触发词

**正文结构**（祈使句，示例 > 规则）：
- 快速开始 - 核心流程的 3-5 步骤
- 示例 - 1-3 个具体输入/输出对
- 参考资料 - 一级引用：see references/foo.md

### 5.3 去重逻辑

执行沉淀前，先检查是否存在同名或重叠技能：

1. 搜索现有技能的 name 和 description
2. 计算与候选者的重叠度（基于触发词和描述）
3. 根据重叠度决定：
   - >80% 重叠 -> 更新现有技能（追加内容而非覆盖）
   - 30%-80% 重叠 -> 可能是相关技能，创建新的但交叉引用
   - <30% 重叠 -> 创建全新技能

### 5.4 质量检查清单

每个创建的技能必须通过以下全部检查项：

- [ ] name 是小写 kebab-case，与目录名一致
- [ ] description 包含 WHAT + WHEN，第三人称，<=1024 字符，包含触发词
- [ ] 正文 <500 行
- [ ] 文件引用一级深度（SKILL.md -> references/foo.md）
- [ ] 无时间敏感信息（如 2025年8月前）
- [ ] 正文使用正斜杠路径（如 scripts/foo.py）
- [ ] 示例具体（Input: X -> Output: Y，而非描述输出）

### 5.5 日志记录

所有决策（包括丢弃）都记录到日志：

~/.agents/skills/skill-evolver/evolution-log.jsonl

每条记录格式：
{
  timestamp: 2026-07-20T22:00:00Z,
  mode: post-task | backlog-review | manual,
  task_summary: 一行描述审查的工作流,
  tool_count: 12,
  tool_sequence: [shell_command,Read,shell_command,apply_patch],
  reusable: true,
  criteria: {...},
  dedup: {...},
  action: created | updated | discarded,
  skill_name: ...,
  reason: ...
}

### 5.6 每日定时回顾（后台审核）

使用 Windows 计划任务每天自动触发 Backlog Review：

powershell
创建每日执行计划
 = New-ScheduledTaskAction -Execute powershell.exe -Argument -NoProfile -Command & C:\Users\shenl\.agents\skills\skill-evolver\scripts\extract_day.ps1 yesterday
 = New-ScheduledTaskTrigger -Daily -At 23:00
Register-ScheduledTask -TaskName Codex Daily Skill Review -Action  -Trigger  -Description Codex skill evolution daily backlog review -RunLevel Limited

回顾流程：
1. 调用 extract_day.py 生成当日候选
2. 对每个候选应用六项标准和去重逻辑
3. 沉淀或丢弃
4. 记录所有决策到日志

### 5.7 禁止事项

- 不要 沉淀一次性任务（修一个 typo、读一个文件）
- 不要 为任何称职的模型默认能做到的事情创建技能
- 不要 重复——始终先做去重，优先更新而非创建
- 不要 让自演化延迟面向用户的工作
- 不要 在技能中硬编码路径/名称——泛化它们
- 不要 记录凭据、API 密钥或临时会话状态

---

## 6. 参考文献

- Skill Evolution 主技能：C:\Users\shenl\.agents\skills\skill-evolver\SKILL.md
- 提取日活动脚本：C:\Users\shenl\.agents\skills\skill-evolver\scripts\extract_day.py
- 进化日志：C:\Users\shenl\.agents\skills\skill-evolver\evolution-log.jsonl
- Lucid Review 方法论：C:\Users\shenl\.agents\skills\skill-evolver\references\lucid-review.md
