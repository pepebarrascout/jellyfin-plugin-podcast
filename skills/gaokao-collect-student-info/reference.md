# 智能推荐志愿表 API 速查

## 接口

| 项 | 值 |
|----|-----|
| 路径 | `POST /zp/volunteer/intelligenceVolunteer` |
| Content-Type | `application/json` |
| 认证 | 无（公网 skill-test 接口） |
| 默认 BASE_URL | `https://publicapi.chatglm.cn/chatglm_public/skill-test` |
| 完整地址 | `{BASE_URL}/zp/volunteer/intelligenceVolunteer` |

## 请求体字段

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| province | string | ✓ | 高考省份 |
| classify | string | ✓ | 文科/理科/物理/历史/综合 |
| score | integer | ✓ | 高考成绩 |
| batch | string | ✓ | 填报批次 |
| subjects | string \| null | 模式相关 | 逗号分隔选科 |
| gradeType | string \| null | 京沪津 | 本科 / 专科 |
| rank | integer \| null | | 位次 |
| universitys | string \| null | | 心仪高校，`,` 分隔 |
| provinces | string \| null | | 省份意向 |
| tags | string \| null | | 院校属性意向 |
| majorClass | string \| null | | 专业类意向 |
| universityNum | integer | | 推荐院校数，默认 30 |
| majorNum | integer | | 每组专业数，默认 6 |
| isAdjust | boolean | | 是否调剂 |
| volunteerType | string | | ACADEMY_GROUP / ACADEMY_MAJOR / MAJOR_GROUP |
| returnUniversityNum | integer | | 已填志愿数 |
| intentionNum | integer | | 已填意向数 |

## 省份与模式

**老高考**：新疆 — classify `文科`|`理科`，subjects/gradeType 为 null。

**3+1+2**：广东,江苏,河北,湖北,湖南,福建,辽宁,重庆,甘肃,黑龙江,吉林,安徽,江西,贵州,广西,云南,内蒙古,四川,宁夏,山西,河南,陕西,青海 — classify `物理`|`历史`，subjects 三科。

**3+3**：上海,北京,天津,山东,浙江,海南 — classify `综合`。浙江可选 `技术`。仅京沪津需 gradeType（本科/专科）。

**批次 batch（测试环境实测，与「本科批」不同）**：

| 省份 | batch |
|------|-------|
| 天津 | 本科批A段 |
| 浙江 | 普通类一段 |
| 山东 | 普通类一段 |
| 四川、云南 | 本科批B段 |
| 甘肃 | 本科批C段 |
| 宁夏 | 本科批B段 |
| 新疆 | 本科一批 |
| 西藏 | 测试环境暂不支持（2222010 高考省份错误） |

## 响应结构

公网 skill-test 接口返回 `{ status, message, result }`（`status=0` 为成功），脚本会自动转换为统一格式。

```json
{
  "status": 0,
  "message": "success",
  "result": {
    "province": "...",
    "schoolList": [
      {
        "universityName": "",
        "universityCode": "",
        "universityMajorGroup": "",
        "enrollProbability": 20,
        "type": "CHONG",
        "majorList": [ { "majorName": "", "claim": "", "type": "CHONG" } ]
      }
    ]
  }
}
```

旧版接口格式 `{ code, msg, body }`（`code=200`）同样兼容。

`type` 枚举：`CHONG` 冲、`WEN` 稳、`BAO` 保、`NAN` 难、`YI` 易。

`score` / `parityScore` 常为 JSON 字符串，如 `[{"2025":"660,2153,60"}]`（分,位次,计划数）。
