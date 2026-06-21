#!/usr/bin/env python3
"""调用智能推荐志愿表 API，解析响应并输出结构化 JSON。"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

# ========== 接口配置（修改此处即可切换环境）==========
BASE_URL = "https://publicapi.chatglm.cn/chatglm_public/skill"
API_PATH = "/zp/volunteer/intelligenceVolunteer"
# =====================================================

TYPE_LABELS = {
    "CHONG": "冲",
    "WEN": "稳",
    "BAO": "保",
    "NAN": "难",
    "YI": "易",
}

# 测试环境下部分省份 batch 需特殊取值，否则会返回「没有可填报的批次」
PROVINCE_BATCH: dict[str, str] = {
    "天津": "本科批A段",
    "浙江": "普通类一段",
    "山东": "普通类一段",
    "四川": "本科批B段",
    "云南": "本科批B段",
    "甘肃": "本科批C段",
    "宁夏": "本科批B段",
    "新疆": "本科一批",
}


def resolve_batch(province: str | None, batch: str | None) -> str | None:
    """根据省份自动修正 batch。

    规则：
    - batch 为空：若省份在映射中，自动补上
    - batch 为「本科批」：若省份在映射中，自动替换为实测可用值
    - 其他：保持不变
    """
    if not province:
        return batch
    mapped = PROVINCE_BATCH.get(province)
    if not mapped:
        return batch
    if batch is None:
        return mapped
    if str(batch).strip() == "本科批":
        return mapped
    return batch


def _parse_json_field(value: Any) -> Any:
    if value is None or value == "":
        return None
    if isinstance(value, (dict, list)):
        return value
    if isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return value
    return value


def parse_score_history(score_str: Any) -> list[dict[str, str]]:
    """将 score / parityScore 字段解析为 [{year, score, rank, plan_num}, ...]。"""
    parsed = _parse_json_field(score_str)
    if not isinstance(parsed, list):
        return []

    rows: list[dict[str, str]] = []
    for item in parsed:
        if not isinstance(item, dict):
            continue
        for year, triple in item.items():
            parts = str(triple).split(",")
            rows.append(
                {
                    "year": str(year),
                    "score": parts[0] if len(parts) > 0 else "-",
                    "rank": parts[1] if len(parts) > 1 else "-",
                    "plan_num": parts[2] if len(parts) > 2 else "-",
                }
            )
    return rows


def parse_parity_scores(parity_str: Any) -> list[dict[str, str]]:
    parsed = _parse_json_field(parity_str)
    if not isinstance(parsed, list):
        return []
    rows: list[dict[str, str]] = []
    for item in parsed:
        if isinstance(item, dict):
            for year, score in item.items():
                rows.append({"year": str(year), "score": str(score)})
    return rows


def normalize_major(major: dict[str, Any]) -> dict[str, Any]:
    type_code = major.get("type") or ""
    return {
        "major_name": major.get("majorName") or "",
        "major_code": major.get("majorCode") or "",
        "major_num": major.get("majorNum"),
        "claim": major.get("claim") or "",
        "study_year": major.get("studyYear") or "",
        "study_cost": major.get("studyCost") or "",
        "major_class": major.get("majorClass") or "",
        "major_remarks": major.get("majorRemarks") or "",
        "plan_num": major.get("planNum"),
        "enroll_probability": major.get("enrollProbability"),
        "type": type_code,
        "type_label": TYPE_LABELS.get(type_code, type_code or "—"),
        "score_history": parse_score_history(major.get("score")),
        "parity_scores": parse_parity_scores(major.get("parityScore")),
    }


def normalize_school(school: dict[str, Any], index: int) -> dict[str, Any]:
    type_code = school.get("type") or ""
    majors = school.get("majorList") or []
    return {
        "index": index,
        "university_name": school.get("universityName") or "",
        "university_code": school.get("universityCode") or "",
        "major_group": school.get("universityMajorGroup") or "",
        "logo": school.get("logo") or "",
        "category_name": school.get("categoryName") or "",
        "property_name": school.get("propertyName") or "",
        "tags": school.get("tags") or "",
        "city_name": school.get("cityName") or "",
        "province_name": school.get("provinceName") or "",
        "enroll_probability": school.get("enrollProbability"),
        "lowest_score": school.get("lowestScore"),
        "type": type_code,
        "type_label": TYPE_LABELS.get(type_code, type_code or "—"),
        "plan_num": school.get("planNum"),
        "level": school.get("level") or "",
        "is_adjust": school.get("isAdjust"),
        "score_history": parse_score_history(school.get("score")),
        "parity_scores": parse_parity_scores(school.get("parityScore")),
        "majors": [normalize_major(m) for m in majors],
    }


def group_schools_by_type(schools: list[dict[str, Any]]) -> dict[str, list[dict[str, Any]]]:
    order = ["CHONG", "WEN", "BAO", "YI", "NAN"]
    grouped: dict[str, list[dict[str, Any]]] = {k: [] for k in order}
    other: list[dict[str, Any]] = []
    for school in schools:
        t = school.get("type") or ""
        if t in grouped:
            grouped[t].append(school)
        else:
            other.append(school)
    if other:
        grouped["_OTHER"] = other
    return grouped


def normalize_api_response(raw: dict[str, Any]) -> dict[str, Any]:
    """兼容新旧两种响应信封：{code,msg,body} 与 {status,message,result}。"""
    if "result" in raw and "status" in raw:
        status = raw.get("status")
        msg = raw.get("message") or ""
        if status != 0:
            raise ValueError(f"API 返回异常: status={status}, message={msg}")
        return {"code": 200, "msg": msg or "OK", "body": raw.get("result") or {}}
    return raw


def _empty_to_none(value: Any) -> Any:
    if value == "" or value is None:
        return None
    return value


def parse_api_response(raw: dict[str, Any]) -> dict[str, Any]:
    raw = normalize_api_response(raw)
    code = raw.get("code")
    msg = raw.get("msg") or ""
    if code != 200:
        raise ValueError(f"API 返回异常: code={code}, msg={msg}")

    body = raw.get("body") or {}
    schools_raw = body.get("schoolList") or []
    schools = [normalize_school(s, i + 1) for i, s in enumerate(schools_raw)]
    grouped = group_schools_by_type(schools)

    stats = {
        "total": len(schools),
        "chong": len(grouped.get("CHONG", [])),
        "wen": len(grouped.get("WEN", [])),
        "bao": len(grouped.get("BAO", [])),
    }

    return {
        "meta": {
            "generated_from": "intelligenceVolunteer",
            "api_code": code,
            "api_msg": msg,
        },
        "profile": {
            "province": body.get("province"),
            "classify": body.get("classify"),
            "subjects": _empty_to_none(body.get("subjects")),
            "grade_type": _empty_to_none(body.get("gradeType")),
            "score": body.get("score"),
            "rank": body.get("rank"),
            "batch": body.get("batch"),
            "volunteer_type": body.get("volunteerType"),
            "is_adjust": body.get("isAdjust"),
            "university_num": body.get("universityNum"),
            "major_num": body.get("majorNum"),
            "universitys": body.get("universitys"),
            "provinces": body.get("provinces"),
            "tags": body.get("tags"),
            "major_class": body.get("majorClass"),
        },
        "stats": stats,
        "schools": schools,
        "schools_by_type": {
            TYPE_LABELS.get(k, k): grouped[k]
            for k in grouped
            if grouped[k] and not k.startswith("_")
        },
        "raw_body": body,
    }


def load_payload_from_student(student_path: Path) -> dict[str, Any]:
    from build_api_request import student_to_api_payload

    student = json.loads(student_path.read_text(encoding="utf-8"))
    return student_to_api_payload(student)


def build_request_payload(args: argparse.Namespace) -> dict[str, Any]:
    if args.student:
        return load_payload_from_student(args.student)
    if args.config:
        with open(args.config, encoding="utf-8") as f:
            return json.load(f)

    payload: dict[str, Any] = {
        "province": args.province,
        "classify": args.classify,
        "subjects": args.subjects,
        "gradeType": args.grade_type,
        "score": args.score,
        "rank": args.rank,
        "batch": args.batch,
        "universitys": args.universitys,
        "provinces": args.provinces,
        "tags": args.tags,
        "majorClass": args.major_class,
        "universityNum": args.university_num,
        "majorNum": args.major_num,
        "isAdjust": args.is_adjust,
        "volunteerType": args.volunteer_type,
        "returnUniversityNum": args.return_university_num,
        "intentionNum": args.intention_num,
    }
    return payload


def api_url(base_url: str | None = None) -> str:
    root = (base_url or BASE_URL).rstrip("/")
    path = API_PATH if API_PATH.startswith("/") else f"/{API_PATH}"
    return root + path


def call_api(
    payload: dict[str, Any],
    base_url: str | None = None,
    timeout: int = 60,
) -> dict[str, Any]:
    url = api_url(base_url)
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = Request(
        url,
        data=data,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "Accept": "*/*",
        },
    )
    try:
        with urlopen(req, timeout=timeout) as resp:
            charset = resp.headers.get_content_charset() or "utf-8"
            text = resp.read().decode(charset)
    except HTTPError as e:
        detail = e.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {e.code}: {detail}") from e
    except URLError as e:
        raise RuntimeError(f"网络请求失败: {e.reason}") from e

    return json.loads(text)


def main() -> int:
    parser = argparse.ArgumentParser(description="获取并解析智能推荐志愿表")
    parser.add_argument(
        "--student",
        type=Path,
        help="student.json 路径（自动映射倾向到 API 选填参数，优先于 --config）",
    )
    parser.add_argument(
        "--config",
        type=Path,
        help="完整请求 JSON 文件路径",
    )
    parser.add_argument(
        "--base-url",
        default=os.environ.get("ZP_API_BASE_URL", BASE_URL),
        help=f"API 根地址（默认脚本内 BASE_URL: {BASE_URL}）",
    )
    parser.add_argument("-o", "--output", type=Path, required=True, help="解析结果 JSON 输出路径")
    parser.add_argument("--save-raw", type=Path, help="可选：保存 API 原始响应")

    parser.add_argument("--province", help="高考省份")
    parser.add_argument("--classify", help="选科模式：文科/理科/物理/历史/综合")
    parser.add_argument("--subjects", help="选科组合，逗号分隔，如 物理,化学,生物")
    parser.add_argument("--grade-type", dest="grade_type", help="成绩类型：本科/专科（京沪津专科批）")
    parser.add_argument("--score", type=int, help="高考成绩")
    parser.add_argument("--rank", type=int, help="高考位次")
    parser.add_argument("--batch", help="填报批次，如 本科批")
    parser.add_argument(
        "--auto-batch",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="是否按省份自动补全/修正 batch（默认开启）",
    )
    parser.add_argument("--universitys", help="心仪高校，逗号分隔")
    parser.add_argument("--provinces", help="省份意向，逗号分隔")
    parser.add_argument("--tags", help="院校属性意向，逗号分隔")
    parser.add_argument("--major-class", dest="major_class", help="专业类意向，逗号分隔")
    parser.add_argument("--university-num", type=int, default=30)
    parser.add_argument("--major-num", type=int, default=6)
    parser.add_argument(
        "--is-adjust",
        action=argparse.BooleanOptionalAction,
        default=True,
    )
    parser.add_argument("--volunteer-type", default="ACADEMY_GROUP")
    parser.add_argument("--return-university-num", type=int, default=30)
    parser.add_argument("--intention-num", type=int, default=0)

    args = parser.parse_args()

    try:
        payload = build_request_payload(args)
    except (OSError, json.JSONDecodeError) as e:
        print(f"错误: 无法读取请求配置 — {e}", file=sys.stderr)
        return 1

    if not args.student and not args.config:
        required = ["province", "classify", "score", "batch"]
        missing = [k for k in required if payload.get(k) is None]
        if missing:
            print(
                f"错误: 缺少必填参数 {missing}，请使用 --student、--config 或补全命令行",
                file=sys.stderr,
            )
            return 1

    # CLI 中 null 用字符串 "null" 传入时转为 None
    for key in ("subjects", "gradeType", "rank", "universitys", "provinces", "tags", "majorClass"):
        if payload.get(key) == "null":
            payload[key] = None

    # 自动修正 batch（可通过 --no-auto-batch 关闭）
    if args.auto_batch:
        payload["batch"] = resolve_batch(payload.get("province"), payload.get("batch"))

    print(f"请求: {api_url(args.base_url)}", file=sys.stderr)
    try:
        raw = call_api(payload, base_url=args.base_url)
    except (RuntimeError, json.JSONDecodeError) as e:
        print(f"错误: {e}", file=sys.stderr)
        return 1

    if args.save_raw:
        args.save_raw.parent.mkdir(parents=True, exist_ok=True)
        args.save_raw.write_text(json.dumps(raw, ensure_ascii=False, indent=2), encoding="utf-8")

    try:
        parsed = parse_api_response(raw)
    except ValueError as e:
        print(f"错误: {e}", file=sys.stderr)
        return 1

    parsed["request"] = payload
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(parsed, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"已保存解析结果: {args.output}（共 {parsed['stats']['total']} 所院校）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
