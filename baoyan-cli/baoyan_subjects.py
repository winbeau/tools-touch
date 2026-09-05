"""Explainable subject preferences; labels are heuristics, not official disciplines."""
import re


EDUCATION = ("教育学院", "教育学部", "教育科学", "教育研究", "高等教育", "未来教育", "国优计划", "教师培养")
FINANCE = ("经济", "管理", "商学院", "财经", "财政", "金融", "会计", "贸易", "工商", "证券", "旅游", "营销", "财务", "审计", "保险")
HUMANITIES = ("外国语", "外语", "英语", "日语", "德语", "法语", "阿拉伯", "东方语", "翻译", "语言", "中文", "华文", "文学", "文学院", "历史", "考古", "哲学", "社会", "政治", "法学院", "法律", "政法", "司法", "马克思", "新闻", "传播", "传媒", "文化", "人文", "公共政策", "人口", "治理", "人类学", "博雅", "岳麓", "东北亚", "国别", "国际问题", "国际关系")
ARTS = ("体育", "艺术", "音乐", "设计")
MEDICAL = ("医学", "医院", "医学院", "药学", "药学院", "药物", "护理", "卫生", "口腔", "临床", "营养", "健康", "中医", "肿瘤", "创新药")
AGRICULTURE = ("农学院", "植物保护", "园艺", "兽医", "林学院", "动物科学", "农业")
ENGINEERING = ("工程", "工学", "理工", "计算", "电子", "电气", "机电", "机械", "软件", "人工智能", "智能", "网络", "信息", "通信", "光电", "自动化", "控制", "仪器", "航空", "航天", "宇航", "空天", "低空", "无人", "机器人", "制造", "材料", "能源", "水利", "土木", "测绘", "遥感", "建筑", "建造", "交通", "运输", "电力", "集成电路", "芯片", "半导体", "纺织", "冶金", "钢铁", "储能", "密码", "高铁", "纳米智造", "未来技术", "先进技术", "先进结构", "碳中和", "物流", "现代邮政", "公路", "民航", "稀土")
SCIENCE = ("数学", "数理", "统计", "物理", "化学", "化工", "生物", "生命", "生态", "环境", "地理", "地球", "地质", "海洋", "天文", "空间科学", "理学院", "力学", "系统科学", "数据科学", "量子", "光谱", "同步辐射", "激光", "分子", "微结构", "黄土", "分析测试", "气候", "河口", "海岸", "水科学", "食品", "神经科学")
CROSS = ("信息管理", "管理科学", "工程管理", "应急管理与安全工程", "科技史", "智能交互", "数智健康", "智能健康", "医学磁共振", "心理", "认知")


def classify_subject(record):
    academy = re.sub(r"\s+", "", record.get("学院") or "")
    # Remove the university from the title: 外国语大学/财经大学 can have STEM units.
    title = re.sub(r"^\s*【[^】]*】[—－\-\s]*", "", record.get("标题") or "")
    title = re.sub(r"\s+", "", title)
    text = academy + "；" + title
    source = "学院及通知标题" if academy else "通知标题（学院字段为空）"

    def result(tag, recommendation, reason):
        return {"学科筛选标签": tag, "筛选建议": recommendation, "筛选依据": source + "：" + reason + "；名称规则初筛，非官方学科认定"}

    def hit(words):
        return next((word for word in words if word in text), None)

    if word := hit(EDUCATION):
        return result("教育类", "保留", f"命中“{word}”，按用户要求保留教育类")
    if word := hit(CROSS):
        return result("交叉学科", "待核实", f"命中“{word}”，需按具体专业判断理工或社科方向")
    if word := hit(FINANCE):
        return result("经管财经", "排除", f"命中“{word}”")
    engineering = hit(ENGINEERING)
    humanities = hit(HUMANITIES + ("新媒体", "非洲学院"))
    arts = hit(ARTS)
    if engineering and (humanities or arts):
        return result("交叉学科", "待核实", f"同时命中理工“{engineering}”与文艺“{humanities or arts}”")
    if humanities:
        return result("文史社科及外语", "排除", f"命中“{humanities}”")
    if arts:
        return result("体育艺术设计", "排除", f"命中“{arts}”")
    # Engineering degrees in medicine/agriculture remain relevant to this preference.
    engineering = engineering or hit(("核科学", "深空探测", "未来城市", "土地科学技术", "功能纳米"))
    if engineering and not (hit(MEDICAL) or hit(AGRICULTURE)):
        return result("工科", "保留", f"命中“{engineering}”")
    if "工程" in text and (hit(MEDICAL) or hit(AGRICULTURE)):
        return result("工科（医农交叉）", "保留", "医农相关学院名称同时明确包含“工程”")
    if word := hit(MEDICAL):
        return result("医学药学", "排除", f"命中“{word}”，未明确工程方向")
    if word := hit(AGRICULTURE):
        return result("农林类", "排除", f"命中“{word}”，未明确工程方向")
    if word := hit(SCIENCE):
        return result("理科", "保留", f"命中“{word}”")
    return result("全校或方向不明", "待核实", "名称不足以确定学科，不按大学名称直接排除")


def add_subject_labels(record):
    labels = classify_subject(record)
    result = {}
    for key, value in record.items():
        result[key] = value
        if key == "学院":
            result.update(labels)
    if "学院" not in record:
        result.update(labels)
    return result
