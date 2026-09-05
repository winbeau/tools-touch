# 保研信息网 CLI

按学校完整名称导出网站现存夏令营、预推免信息，支持 Excel、CSV（UTF-8 BOM）、JSON。报名开始/截止、活动开始/结束、官网链接、报名链接均单独列出。

## 使用

```sh
cd /home/winbeau/Projects/tools-touch/baoyan-cli
./baoyan export --school 北京大学 --year 2026 -o exports/北京大学-2026.xlsx
./baoyan export --school 北京大学 --year 2026 -o exports/北京大学-2026.csv
./baoyan export --school 清华大学 --kind 预推免 -o exports/清华大学-预推免.xlsx
./baoyan export --school 北京大学 --anonymous -o exports/北京大学-全部年份.json
```

默认同时选择夏令营、预推免，不限年份，不限报名状态。`--force` 覆盖已有文件。`--anonymous` 不读取登录凭据。

## 985／211 预推免分组报表

```sh
# 从保研信息网公开接口重新查询，输出到自动生成的时间戳目录
./baoyan report --year 2026 --deadline-from 2026-09-05

# 复用已有快照重新排版，不联网；输出目录必须不存在
./baoyan report --year 2026 --deadline-from 2026-09-05 \
  --snapshot exports/985-211-预推免-2026-截止0905起-20260905-070218 \
  --output-dir exports/985-211-预推免-2026-按大学分组

./baoyan report --help
```

实现脚本为 `baoyan_report.py`，也可用 `uv run python baoyan_report.py` 加相同参数执行；旧的 `exports/query_985_211_deadlines.py` 入口仍可用。

- 筛选使用**报名截止时间**；排序使用**活动结束时间**，两者独立。
- 排序优先级：985 → 非985的211 → 大学名称全拼 A–Z → 同校活动结束时间降序。大学名称连续排列；校区相邻，保留原始名称；活动时间为空或无效时放在该校最后。不使用商业大学排名。
- Excel 同校整行同底色，校区与母校同色；各表使用相同颜色映射，冻结表头并启用筛选。CSV／JSON 保持同样的明细顺序，不包含颜色。
- 新增“学科筛选标签／筛选建议／筛选依据”：按学院和去掉大学名的通知标题做名称规则初筛，保留工科、理科和教育类；经管财经、文史社科外语、医药农林体艺建议排除；交叉或全校通知待核实。教育类例外优先，医农工程交叉保留；不会因为学校叫财经／外国语大学就排除其计算机等学院。规则在 `baoyan_subjects.py`，属于可复核的偏好标签，非官方学科认定。
- 第一页“优先保留”供直接浏览，“匹配明细”保留全部日期匹配记录及标签，“学科待核实”“建议排除”可回看；原有“待核实”仍专指日期／年份／院校身份问题。
- 固定列宽、固定单行行高，不自动换行；长内容缩小字体以适应单行（很长的链接／说明可在编辑栏查看）。保留已有边框，未设置边框的单元格补细边框；学校和活动结束时间列加粗。Excel显示时将原始换行／制表符替换为空格，JSON原文保持不变。
- 生成匹配明细、学校汇总、待核实、院校覆盖和查询说明；同时保存原始数据，供 `--snapshot` 复用。学校汇总按母校合并校区。
- 年份缺失、学校身份不明、截止年份冲突分别标记；不将未知数据判为已经结束。985／211 用学校名称名单识别，避免网站层次误标；覆盖范围仍限于网站现存通知。
- 导出先写临时目录，全部成功后才生成最终目录；拒绝覆盖既有目录。`--deadline-from` 的年份必须等于 `--year`。

验证：`uv run python -m unittest test_baoyan test_baoyan_report`。

## 登录

实测公开 `/articles` 列表已包含导出所需时间、官网字段，可以免登录完成导出。若接口将来要求登录，或希望使用自己的账号：

```sh
./baoyan login
# 已有本工具打开的专用调试浏览器时：
./baoyan login --cdp http://127.0.0.1:9229
```

Chrome 中微信扫码后自动保存令牌，不需要复制密码或 Cookie。登录信息保存在 `~/.config/baoyan-cli/session.json`，文件权限 0600；专用浏览器资料位于 `~/.local/share/baoyan-cli/chrome`。也支持 `BAOYAN_TOKEN` 环境变量。不要将登录信息提交到仓库。登录过期会明确报错，不会静默切换账号或权限。

## 数据含义与完整性

- 数据来源：http://pc.baoyanwang.com.cn/ ，API 为 `http://api.baoyanwang.com.cn/api/v1`。脚本使用当前公开前端的 nonce/MD5 签名算法。
- `/articles?college=北京大学&category=保研信息&all=1&page=1&size=100` 按学校分页；实测 `all=1` 会忽略 `tag`，所以拉取该校全部分页后本地精确匹配标签。
- 根据接口 `total_count` 校验分页，重复记录、提前空页、总数变化时停止，避免把部分结果当成全部。
- “全部”指网站当前保留的记录，不保证覆盖学校全部官方公告或历史版本；网站可修改既有信息。
- `--year 2026` 精确匹配网站 `year` 字段，不是根据标题中“2027级”推断。年份为 0 的记录会提示并排除；不指定年份即可保留。不要把抓取日期、更新日期当成活动年份。
- 官网字段来自 `office_url`，报名链接来自 `sign_up_url`；原始日期不做猜测或时区转换。本站未填写的字段留空并注明；未逐条到院校官网核实。
- 仅访问查询接口，不调用收藏、支付等接口。请求默认间隔 0.3 秒，网络错误/429/5xx 最多重试 3 次。CSV/XLSX 对公式前缀作转义。
- 使用临时文件完成导出后原子替换目标，失败不会留下一份貌似完整的表格。

## 新环境安装

需要 Python 3.10+、uv 和 Google Chrome：

```sh
uv venv .venv
uv pip install --python .venv/bin/python -r requirements.txt
chmod +x baoyan
./baoyan --help
```

无需下载 Playwright 自带 Chromium；登录使用已安装的 Chrome。纯导出只依赖 httpx；Excel 输出另需 openpyxl。
