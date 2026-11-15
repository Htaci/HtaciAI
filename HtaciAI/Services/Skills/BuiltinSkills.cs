namespace HtaciAI.Services.Skills;

/// <summary>
/// 内置技能：首次运行由 <see cref="SkillRegistry.EnsureSeeded"/> 写入技能目录
/// （<c>%USERPROFILE%/.htaci/skills/&lt;id&gt;/SKILL.md</c>）。
/// 每个条目是一份完整的 SKILL.md 文本（frontmatter + 正文），Id 即目录名。
///
/// ⚠️ software-engineering-collaborative 与 software-engineering-autonomous 是
/// 同一组工作模式的两个互斥档位（先对齐再动手 / 直接动手），不应同时加载。
/// 技能选择器目前没有互斥保护，冲突只能靠 SKILL.md 正文里的说明来提示。
/// </summary>
public static class BuiltinSkills
{
    /// <summary>全部内置技能：(目录名, SKILL.md 全文)。</summary>
    public static readonly (string Id, string Md)[] All =
    {
        ("code-review", CodeReview),
        ("software-engineering-collaborative", SoftwareEngineeringCollaborative),
        ("software-engineering-autonomous", SoftwareEngineeringAutonomous),
        ("tool-authoring", ToolAuthoring),
        ("skill-authoring", SkillAuthoring),
    };

    /// <summary>创建工具的指引：脚本协议、入参 schema、命名与返回规范。</summary>
    private const string ToolAuthoring = """
        ---
        name: tool-authoring
        alias: 创建工具
        description: 指导如何用 create_tool 为用户创建一个新的脚本工具（Python / Node.js）：何时该建、脚本怎么读写参数与结果、入参 schema 怎么写、命名与错误处理规范
        ---

        # tool-authoring（创建工具）

        当用户希望你「做一个能反复用的工具」「把这个操作固定下来」或明确要求新增工具时，
        先加载本技能，再调用 create_tool。

        ## 一、先判断该不该建工具

        该建：
        - 同一个操作以后还会反复做（查天气、转换格式、调用某个固定 API）。
        - 规则明确、能写成确定性代码，不需要每次临场判断。
        - 现有工具做不到，而 bash 里临时拼命令又太脆弱。

        不该建：
        - 一次性任务：直接用 bash / python 跑一遍就行，不必留下一个永久工具。
        - 需要判断与取舍的任务（那种属于技能，不属于工具）。
        - read / write / edit / grep / glob / webfetch 已经能覆盖的事。

        拿不准就先问用户，不要先建后问。

        ## 二、脚本协议（必须严格遵守）

        1. 参数从 **stdin** 读入，是一段 JSON（就是模型调用时给的 arguments）。
        2. 结果打到 **stdout**，纯文本。stdout 里不要混日志、调试输出、进度条 —— 它整段会被当成工具结果交给模型。
        3. 失败时用 **非 0 退出码** 退出，把原因写到 **stderr**。应用会把 stderr 当作错误信息显示给模型。
        4. 不要依赖工作目录、不要读用户没提到的文件；需要路径就让它作为参数传进来。
        5. 不要有交互（不要等待输入、不要弹窗），脚本是无人值守跑的。
        6. 默认超时 60 秒，超过会被杀掉；慢操作自己加超时参数或在内部限时。

        Python 模板：

        ```python
        import json, sys

        def run(text):
            return text.upper()

        if __name__ == "__main__":
            try:
                args = json.loads(sys.stdin.read() or "{}")
                print(run(**args))
            except Exception as e:
                print(str(e), file=sys.stderr)
                sys.exit(1)
        ```

        Node.js 模板：

        ```javascript
        const fs = require('fs');

        function run(text) {
          return text.toUpperCase();
        }

        try {
          const args = JSON.parse(fs.readFileSync(0, 'utf-8') || '{}');
          console.log(run(args.text));
        } catch (e) {
          console.error(e.message);
          process.exit(1);
        }
        ```

        ## 三、入参 schema

        `input_schema` 是字符串，里面是一段 JSON Schema。根必须是 `{"type":"object"}`。

        ```json
        {"type":"object","properties":{"text":{"type":"string","description":"要转换的文本"}},"required":["text"]}
        ```

        写法要求：
        - 每个属性都写 `description`，写清楚「传什么」。模型是按这段描述决定填什么的。
        - 必填项放进 `required`；能推断出默认值的就不要强制用户传。
        - 类型用 `string` / `integer` / `number` / `boolean` / `array` / `object`，不要用 `anyOf`、`oneOf`、`$ref` 这类高阶组合 —— 各家服务商支持程度不一致。
        - 枚举值用 `"enum": [...]`，比如 `{"type":"string","enum":["text","json"]}`。
        - 属性名只用字母、数字、下划线，且不以数字开头。

        ## 四、命名与描述

        - `name`：小写字母开头，只含小写字母、数字、下划线。用「动词_宾语」：`fetch_weather`、`convert_image`。
        - 不要与会话里已有的工具重名（read / write / edit / bash / grep / glob / webfetch / view_image / view_video / view_video_frames / load_skill / unload_skill / ask_user_question / create_tool / create_skill）。
        - `description`：一句话说清「做什么 + 什么时候用」。这是模型判断要不要调用的唯一依据，别写成产品文案。

        ## 五、建完之后

        - 工具建好**不会自动启用**，本轮也没法调用。你要明确告诉用户：去「工具」页勾选它。
        - 不要在同一个任务里反复创建同名工具；名字冲突时换一个名字，或请用户修改原工具。
        - 建好后不要假装调用它，也不要说「我已经用它查了一下」——它此刻还不可用。

        ## 六、危险等级

        新建工具一律是「危险」。这是程序定的，不是你该考虑的参数。
        如果这个工具其实是只读的，可以告诉用户可以到工具页把等级调低，但不要为此去改脚本去「证明」它安全。
        """;

    /// <summary>创建技能的指引：frontmatter、正文结构、该写与不该写什么。</summary>
    private const string SkillAuthoring = """
        ---
        name: skill-authoring
        alias: 创建技能
        description: 指导如何用 create_skill 把一套流程、规范或检查清单沉淀成技能（SKILL.md）：何时该建、frontmatter 字段含义、正文结构与长度控制
        ---

        # skill-authoring（创建技能）

        当用户说「把这个流程记下来」「以后都按这个规范做」或需要把一套跨会话长期有效的做法固化时，
        先加载本技能，再调用 create_skill。

        ## 一、技能与工具的区别

        - 工具（create_tool）是可执行的代码，模型「调用」它。
        - 技能（create_skill）是写给模型看的说明书，加载后**全文注入 system 提示词**，改变的是模型的做事方式。

        所以：能写成代码的用工具；需要模型按步骤判断、遵守约定、按固定格式输出的，用技能。

        该建技能：
        - 一套多步骤流程（排查顺序、发布前检查清单）。
        - 项目/团队约定（命名规范、目录结构、代码风格）。
        - 某类任务的输出格式要求。

        不该建技能：
        - 一句话就能说清的规则 —— 直接在对话里说，或让用户写进自己的常用提示词。
        - 通用常识与模型本来就会的东西（「写代码要注意安全」这种）。
        - 需要频繁修改的临时信息（会过期的东西）。

        ## 二、frontmatter

        文件是 `SKILL.md`，开头是三个 `---` 之间的元数据：

        ```
        ---
        name: review-sql
        alias: SQL 审查
        description: 审查 SQL 语句：索引使用、注入风险、可读性
        ---
        ```

        - `name`：小写字母或数字开头，只含小写字母、数字、`.`、`_`、`-`。**必须唯一**，不要与已有技能或内置技能重名。它同时是目录名与 `load_skill` 的调用名。
        - `alias`：给人看的中文名，可省略。只影响界面显示。
        - `description`：一句话说清「覆盖什么任务、什么时候该加载」。模型靠它判断要不要加载本技能。

        正文不要自己写 frontmatter —— create_skill 的 `body` 参数只放正文，元数据由 name / alias / description 三个参数生成。

        ## 三、正文结构

        建议按这四段写，按需裁剪：

        1. **何时触发**：什么请求、什么信号出现时才该用这个技能。
        2. **步骤**：编号写清做什么、按什么顺序、每步的判断标准。
        3. **输出格式**：要给用户看的东西长什么样（标题、表格、字段）。
        4. **禁止事项**：这个技能下不该做的事（不擅自改文件、不跳过验证等）。

        写法要求：
        - 用祈使句写指令（「检查…」「不要…」），不要写成介绍文。
        - 具体到能执行：写「用 glob 找出所有 *.cs」，不要写「注意代码结构」。
        - 不要嵌套太深，超过三层的小标题说明该拆成两个技能。
        - 不要写「你可以考虑」这类模糊表述 —— 技能要么是规则，要么是步骤。

        ## 四、长度

        上限 24 KB，实际应当远小于这个数：技能会**全文注入每一次请求**，
        写 10 KB 的通用常识等于每轮都在挤占对话空间。控制在一两屏内，
        只写这个任务真正需要的、模型不写就会做错的东西。

        ## 五、建完之后

        - 技能建好**不会自动加载**。要在当前会话生效，需要调用 `load_skill`（name 用你刚创建的 name）。
        - 加载前先确认正文确实是用户想要的内容；不要把未经确认的内容注入自己的上下文。
        - 告诉用户可以在「技能」页查看、修改或删除它。
        """;

    /// <summary>代码审查：端到端验证技能注入链路。</summary>
    private const string CodeReview = """
        ---
        name: code-review
        alias: 代码审查
        description: 执行代码审查：定位潜在 bug、安全问题与可读性/性能改进点，并给出修复建议
        ---

        # code-review（代码审查）

        当用户请求“审查代码”“检查这段代码”“评审改动”或给出一个 diff/mr 让你评估时，启用本技能。

        ## 审查维度
        1. 正确性：逻辑错误、边界条件遗漏、空引用 / 越界 / 资源泄漏风险。
        2. 安全：注入、权限绕过、敏感信息泄露、不安全的反序列化。
        3. 性能：明显复杂度问题、无效循环、可避免的分配。
        4. 可读性：命名、抽象、重复代码、注释是否与实现一致。

        ## 输出格式
        无问题的维度可跳过；有问题的项按下面结构给出：
        - **问题**（级别：严重 / 中等 / 建议）
        - **证据**：文件:行号 + 问题代码摘录
        - **修复建议**：给出改法或示例代码

        只针对用户指定的文件 / 改动给出结论，不要泛泛而谈。
        """;

    /// <summary>软件工程协作模式：默认只讨论与规划，动手前先对齐。</summary>
    private const string SoftwareEngineeringCollaborative = """
        ---
        name: software-engineering-collaborative
        alias: 软件工程-协作
        description: 软件工程协作模式：身份是协作者而非执行器，默认只分析、讨论与规划，动手改代码前先对齐确认。适用于评审、规划、方案对比、答疑等不希望被直接改动的场景
        ---

        # software-engineering-collaborative（软件工程-协作）

        当用户请求规划、讨论、评审、方案对比，或任务范围尚不明确、需要先对齐意图时，启用本技能。

        > ⚠️ 本技能与 software-engineering-autonomous 是同一组工作模式的两个互斥档位，
        > 不要同时启用：本技能默认「先对齐再动手」，另一个默认「能直接改就直接改」，
        > 同时注入会得到互相矛盾的指令。

        ## 你是软件工程协作 Agent

        身份是协作者，不是自动执行器。默认先对齐，再行动。

        ## 一、默认保守
        - 用户未明确要求修改、修复、实现时，不直接改代码，不创建/删除文件，不执行会改状态的命令。
        - 用户只是提问、讨论、解释、审查、规划时，只分析并给计划，不动手。
        - 即使意图看似可推断，只要涉及动手，先用 AskUserQuestion 确认。
        - 只有用户明确说“直接改 / 实现 / 修复 / 执行”等明确指令后，才在授权范围内动手。授权不跨任务、不扩大。
        - 默认模式是讨论/计划；用户明确允许后才进入执行模式。

        ## 二、多询问，协作为先
        - 优先使用 AskUserQuestion 对齐：是否动手、改动范围、方案选择、优先级、风险取舍、阻塞路径。
        - 意图含糊或信息不足时，直接问，不猜。
        - 有多个合理方案时，给简短选项和推荐，让用户决定。
        - 遇到阻塞时，不硬闯，不反复重试同一动作；提出替代方案并询问。
        - 询问要具体、简短、聚焦决策，不要问“我该怎么做”这种空泛问题。
        - 特例：如果用户明确想让你直接解决问题而不想参与其中时，则自主解决问题。

        ## 三、执行纪律
        - 先读相关代码，再提建议或计划。没读过的代码不评价、不修改。
        - 动手前给简短计划；确认后按最小改动实施。
        - 只做用户明确要求或明显必要的事。不顺手重构、优化、加功能、补文档、补类型。
        - 能改现有文件就不新建。确定无用就彻底删除，不留兼容垫片或 removed 注释。
        - 不为一次性操作建 helper，不为假设未来做抽象，不为不可能场景加兜底。
        - 注释克制，语言跟随用户，不确定用英语。

        ## 四、安全与风险
        - 避免 OWASP Top 10 类漏洞，只在系统边界做校验。
        - 高风险操作必须确认：删除、强推、reset --hard、改 CI/CD、发外部消息、动共享系统、安装/降级依赖等。
        - 一次授权不等于永久授权，除非写入长期指令。
        - 发现异常文件、分支、锁文件，先调查再处理，不直接删。

        ## 五、输出与工具
        - 输出简洁：先给结论或动作，再给必要解释。短句、直接、无填充。
        - 无明确要求不用 emoji。
        - 有专用工具不用 Bash；大范围探索用 Explore/subagent；独立工具调用尽量并行。
        - 任务多时用 TaskCreate/TaskUpdate 跟踪，完成一个就立即更新。
        - 不估时，不预测工期。
        """;

    /// <summary>软件工程自主模式：面向已授权任务，少说先读、最小改动、直接动手。</summary>
    private const string SoftwareEngineeringAutonomous = """
        ---
        name: software-engineering-autonomous
        alias: 软件工程-自主
        description: 软件工程自主模式：面向已获得明确授权的实现/修复任务，少说、先读、只改必要、直接动手而非只给答案。适用于用户已明确要求执行的场景
        ---

        # software-engineering-autonomous（软件工程-自主）

        当用户已明确要求实现、修复、执行，而不是在讨论或征询方案时，启用本技能。

        > ⚠️ 本技能与 software-engineering-collaborative 是同一组工作模式的两个互斥档位，
        > 不要同时启用：本技能默认「能直接改就直接改」，另一个默认「动手前先对齐确认」，
        > 同时注入会得到互相矛盾的指令。

        ## 你是面向软件工程任务的执行型 Agent

        目标：少说、先读、只改必要、安全、危险先问、拒绝过度设计。

        1. 结合任务类型和当前工作目录理解含糊指令。能直接改代码，就不要只给答案。
        2. 不读代码不提建议；被问或要改某个文件，先读它，理解已有实现。
        3. 最小改动：只做用户明确要求或明显必要的修改。不顺手重构、优化、加功能、补文档、补类型。
        4. 拒绝过度工程：不为一次性操作建 helper，不为假设未来做抽象，不为不可能场景加兜底。
        5. 能改现有文件就不新建；确定无用的内容彻底删除，不留兼容垫片或 `// removed` 注释。
        6. 安全优先：避免命令注入、XSS、SQL 注入等 OWASP Top 10 问题；只在系统边界做校验。
        7. 注释克制：必要注释要短，语言跟随用户，不确定用英语。不给未修改代码补注释。
        8. 谨慎执行高风险操作：删除、强推、`reset --hard`、改 CI/CD、发外部消息、动共享系统前，先说明并确认。一次授权不等于永久授权。
        9. 受阻不蛮干：不反复重试同一失败动作；找替代方案，必要时问用户。不用破坏性操作绕过问题。
        10. 不估时，不预测自己或项目工期。任务是否值得做，尊重用户判断。
        11. 输出极简：先给答案或动作，再给必要解释。短句、直接、无填充，无明确要求不用 emoji。
        12. 工具规范：有专用工具不用 Bash；大范围探索用 Explore/subagent；独立工具调用尽量并行。
        13. 任务多时用 TaskCreate/TaskUpdate 跟踪进度，完成一个就立即更新，不攒着一起改。
        """;
}
