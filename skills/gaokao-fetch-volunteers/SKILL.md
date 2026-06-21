---
name: gaokao-fetch-volunteers
description: >-
  调用高考智能推荐志愿表 API，根据考生基本信息及专业/城市/院校倾向（映射为 API 选填参数）
  获取冲稳保志愿列表，解析为 parsed.json。适用于获取推荐院校、冲稳保志愿表、志愿 API 调用。
---

# 获取推荐志愿表

本 Skill 是流水线的**第二步**：读取 `student.json`，**提取并映射考生倾向到 API 选填参数**，调用志愿接口，输出 `parsed.json`。

## 上下游

- **上游**：[gaokao-collect-student-info](../gaokao-collect-student-info/SKILL.md) → `student.json`
- **下游**：[gaokao-recommend-majors](../gaokao-recommend-majors/SKILL.md)、[gaokao-recommend-schools](../gaokao-recommend-schools/SKILL.md)、[gaokao-generate-report](../gaokao-generate-report/SKILL.md)

## 环境准备

```bash
cd gaokao-fetch-volunteers
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
```

## 执行步骤

### 1. 从辅助信息提取倾向（Agent 必做）

读取 `student.json`，根据 [preference_mapping.md](preference_mapping.md) 将考生倾向补全/写入以下字段：

| 字段 | 映射到 API |
|------|-----------|
| `preferred_universities` | `universitys` |
| `preferred_provinces` / `preferred_cities` | `provinces` |
| `preferred_tags` | `tags` |
| `preferred_major_classes` | `majorClass` |

提取来源：`interests`、`career_direction`、`preferred_cities`、`notes` 及对话中的院校/专业/层次偏好。

若 Step1 已结构化写入，本步核对并补全；**调用 API 前倾向字段不得为空数组**（考生无偏好时除外）。

### 2. 构建 API 请求

```bash
python3 scripts/build_api_request.py \
  -i output/student.json \
  -o output/api_request.json \
  --summary output/preference_summary.json
```

脚本将 `preferred_*` 转为 API 选填参数；`preferred_cities` 会自动推导 `provinces`（见 `preference_mapping.md`）。

### 3. 调用志愿 API

```bash
python3 scripts/fetch_volunteers.py \
  --config output/api_request.json \
  -o output/parsed.json
```

或一步完成（内置构建逻辑）：

```bash
python3 scripts/fetch_volunteers.py \
  --student output/student.json \
  -o output/parsed.json
```

脚本会按省份自动修正 `batch`（如山东 → `普通类一段`）。

### 4. 向用户说明

结合 `preference_summary.json` 与 `parsed.json` 的 `stats`，说明：

- 传入了哪些倾向参数（院校/省份/层次/专业类）
- 冲/稳/保各多少所

## API 选填参数说明

| API 字段 | 含义 | 来源 |
|----------|------|------|
| `universitys` | 心仪高校 | `preferred_universities` |
| `provinces` | 省份意向 | `preferred_provinces` 或由城市推导 |
| `tags` | 院校属性 | `preferred_tags`（985/211 等） |
| `majorClass` | 专业类意向 | `preferred_major_classes` |

完整 API 文档见 [reference.md](reference.md)。

## 输出结构（parsed.json）

| 字段 | 说明 |
|------|------|
| `profile` | 含传入的选填参数回显 |
| `stats` | 冲/稳/保数量 |
| `schools_by_type` | 分组院校列表 |
| `request` | 实际 API 请求体（含倾向参数） |

## 故障排查

| 现象 | 处理 |
|------|------|
| 推荐结果与倾向不符 | 检查 `api_request.json` 中选填参数是否正确 |
| 没有可填报的批次 | 查 [reference.md](reference.md) |
| 倾向未传入 | 确认 Step1/本步已填写 `preferred_*` 字段 |

## 附加资源

- [preference_mapping.md](preference_mapping.md) — 倾向 → API 参数映射规则
- [reference.md](reference.md) — API 与 batch 速查
- [examples/api_request_shandong.json](examples/api_request_shandong.json) — 含选填参数示例
