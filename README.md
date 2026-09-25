# KismetDecompiler — UE5 蓝图 Kismet 字节码反编译器

基于 **UAssetAPI**（`KismetExpression[]` AST）的 Unreal 蓝图反编译工具：
把 `FunctionExport.ScriptBytecode`（已解析的 Kismet 字节码表达式树）还原成接近原始蓝图/C++ 的可读伪代码。

目标只有一个：**可读性**。所有优化都只在能证明「语义等价」的前提下进行，且都作为开关叠加，不破坏基础路径。

## 用法

```
用法: KismetDecompiler --input <蓝图.uasset | 目录> [--usmap <mapping.usmap>] [--out <dir>]
                       [--opt] [--inline] [--jobs N] [--uhtdump <UHTHeaderDump 目录>]

  --input    目标 .uasset 文件，或包含 .uasset 的目录（递归）
  --uasset   同 --input（兼容旧写法）
  --usmap    UE5 unversioned properties 映射文件（.usmap）
  --out      输出目录（默认 ./Decompiled）
  --ext      输出扩展名（默认 .cpp）
  --opt      后处理：临时量值流内联 / 常量折叠 / 临时量改名 / 循环与 goto 整理
  --inline   [实验] dispatch case 体内联回事件函数（产物会膨胀几十倍，仅作参考）
  --jobs    目录模式下的并行度
  --uhtdump  UHT 头文件导出目录；给出后引擎函数的 out 形参会渲染成 `out x`，
             未知数值转换也能按形参类型还原
  --disasm [名字子串]   反汇编视图（字节偏移 / 操作码 / 渲染结果 / 跳转目标）
  --flowdebug / --cfg   执行流栈解析与 CFG 调试
```

示例：

```bash
dotnet run -c Debug -- \
  --input 'H:\.vscode\Output\Exports\kards\Content\Blueprints\Cards\bp_cardfunctions.uasset' \
  --usmap m.usmap --out .cache/out1 --opt \
  --uhtdump 'H:\sbk\KARDS\default\game\kards\Binaries\Win64\ue4ss\UHTHeaderDump'
```

产物：每个资产一个 `.cpp`（含函数索引 + 每个函数的签名、注释、函数体）。

## 流水线

```
ScriptBytecode
   │  FlowStackResolver        把 EX_PushExecutionFlow / EX_PopExecutionFlow 解析成静态跳转
   ▼
BuildStatements → SplitBlocks → BuildEdges → MarkReachable(+DCE) → Compact
   │  FunctionSignatures      从 LoadedProperties 取出真实签名（参数名/类型/in-out/局部变量）
   ▼
ComputeDominators / ComputePostDominators / FindLoops
   ▼
EmitRegion                   支配树 + 后支配 + 自然循环 → if / if-else / while / do-while / switch
   │  EmitPendingGotoTargets  无法结构化处才落 goto + 缩进后的 label
   ▼
ReadabilityPasses            文本级等价改写（见下）
   ▼
SemanticOptimizer（--opt）   值流内联 + 常量折叠 + 命名 + 循环还原 + goto 尾部复制
```

## 可读性手段一览

### 表达式层（`ExprRenderer`）

- **引擎数学库 → 运算符**：`Greater_IntInt(a,b)` → `a > b`，`BooleanAND/OR` → `&&`/`||`，
  `Not_PreBool` → `!`，`Add_IntInt` → `+` … 并按**最小括号化**（`TopLevelPrec`）决定是否加括号，
  不会出现 `(a) && (b)` 这种噪音。
- **动态转换**：`EX_DynamicCast` → `Cast<BP_HandCard>(self)`；`EX_PrimitiveCast` 的
  类型检查 → `x != None`；数值隐式转换 token 3/4 → `(float)` / `(double)`
  （已用 UHT 签名交叉验证）。
- **文本常量**：`EX_TextConst` 按 `EBlueprintTextLiteralType` 还原成
  `"..."` / `NSLOCTEXT(...)` / `LOCTABLE(...)`。
- **名称清洗**：`Default__X_C` → `X`；带空格的蓝图函数名（本例中确实存在
  `Update Operable Color`）→ 合法 C++ 标识符 `Update_Operable_Color`，注释里保留原名。
- **`KismetExpression<T>.RawValue` 递归展开**：`EX_SoftObjectConst` 等内层表达式不再退化成类型名。
- 覆盖了 UAssetAPI 里全部会出现的 `EX_*`（含委托、Set/Map 常量、`EX_ArrayGetByRef`、
  调试指令等）；语料中已无「退化渲染」。

### 控制流层（`StructuredKismetDecompiler`）

- 支配树 + 后支配树（虚拟出口）+ 自然循环 → 结构化 if/else、while、do-while。
- 分支极性判定：`f==t && inner.Follow==t` → 守卫式 `if (!cond) break;`；
  `f==n` → `if (!cond) {…}`；`f==t` → `if (cond) {…}`；否则 if/else 或区域不相交检查。
- 死代码消除 + 只给真正被引用的块插 label；label 与本块语句同缩进。

### 文本层（`ReadabilityPasses`，按趟迭代到不动点）

| 规则 | 效果 |
|---|---|
| `CollapseEmptyIf` | `if (C) {} else {B}` → `if (!C) {B}`；空 `if` 直接删除 |
| `CollapseElseIf` | `else { if (C) {…} }` → `else if (C) {…}`（整条 if/else 链正好填满 else 块时才做） |
| `FlattenNestedIf` | `if (A) { if (B) {…} }` → `if (A && B) {…}`，两侧都不能带 else |
| `DropDeadAfterTerminator` | `return;/break;/continue;/goto` 之后同块内的语句永远到不了 → 删掉 |
| `DropDispatchJumpComment` | 反 Dispatch 后 case 体开头残留的 `// switch (EntryPoint);` 注释 |
| `DropElseAfterTerminator` | `else` 体内以 `return/break/continue` 收尾时去掉 `else` |
| `FoldGuardPreamble` | `while(true){ A=e1;…; if(!C) break; BODY }` → `while(C'){ BODY }` |
| `RecoverForLoop` | `i=init; while(cond){ BODY; i=i+k; }` → `for (i=init; cond; i+=k) { BODY }` |
| `WhileTrueGuardFirst/Last` | `while(true)+guard` ⇄ `do…while`，按守卫位于首/尾选择 |
| `LoopExitBreak`（CFG 层） | 循环出口块渲染成空 `else` 时改写成 `if (!C) break;`，见下 |
| `StripTrailingContinue` / `DropTrailingReturn` / `DropEmptyElse` | 收尾噪音清理 |
| `GotoTailDup`（CFG 层） | 分支汇入兄弟分支内部共用尾部时，直接把那段尾部抄过来，消掉 goto |
| `NegateComparison` | `!(a < b)` → `a >= b`；`!(x)` → `!x` |
| `TidyLine` | 去冗余括号（`if ((X))`）、赋值右侧多余括号 |

`RecoverForLoop` 的成立条件（缺一不可）：步进是该层最后一条语句；循环体内不再给循环变量赋值；
循环体内无 `continue`（`continue` 会跳过步进）；`while` 条件里用到该变量。

#### 循环出口必须是 `break`（`LoopExitBreak`）

蓝图里的 `ForEach` 展开成 `B2: if (i < len) → 循环体 else → B6`，而 `B6` 往往是
**没有语句、只做 `Jump 循环出口` 的空壳块**。它被当成 `loop.Exits` 直接跳过，
于是渲染成 `while (true) { if (C) { BODY } else { } }`；文本层的 `DropEmptyElse` 再把空
`else` 删掉，就只剩 `while (true) { if (C) { BODY } }` —— **条件不成立时什么都不做、回到
循环头，输出里凭空多出一个死循环**。

修法放在 CFG 层（`EmitIf`）：只要分支目标 `t` 满足下面任一条，就写成 `if (!C) break;`：

- `t` 就是本层循环的出口块（`loop.Exits`）；
- `t` 是循环汇合点（`loop.Follow`）；
- 从 `t` 出发只经过「无语句 + 无条件跳转」的空壳块，最终落到 `loop.Follow`。

前提是 `ipdom(guard) == loop.Follow` 且该汇合点不在循环体里，保证 `break` 落点正确。
改完之后 `while (true)` 由 68 降到 45、且**每一个都有真实出口**，其中 23 个进一步被
`WhileTrueGuardFirst` 折叠成 `while (cond)`。

#### goto 消除（`GotoTailDup`）

结构化之后只剩一种 goto：一个分支要汇入兄弟分支内部的共用尾部。因为控制流是任意图，
用文本启发式去猜「抄哪一段」是不可靠的，所以判定放在 **CFG 层**，满足两条硬条件才做：

1. **续接点一致**：`ipdom(goto 所在区域的续接块) == ipdom(目标块)`。
   发射器现在把「每个区域执行完之后跳到哪个块」一路传下来（`EmitRegion(..., follow)`），
   goto 记录自己所在区域的 `follow`；目标块的直接后支配点由后支配树给出。
   两者相同 ⇒ 抄完尾巴落到的地方，和 goto 掉出去落到的地方是同一块代码。
2. **范围精确**：抄的行范围必须正好是「目标标签起 → 该区域结束」，
   由发射期记录的 `_regionEnd`（每个块所属区域的结束行）给出，绝不多抄一行。

文本层只做最后一道自检：尾巴里一旦出现 `break`/`continue`/`goto`/标签就放弃
（这些会随嵌套层级改变含义）。判定为安全的 goto，其标签在所有引用都被替换后自动删除。

> 之所以不敢用纯文本启发式：像
> `if (A) { goto L; } L: tail;` 这种，把 `tail` 抄进 `if` 后，`if` 结束会**再次**落到 `L:`
> 把尾巴执行第二遍。上述第 1 条正好把这种情形排除掉（`ipdom(goto)` 是 `L` 而不是 `ipdom(L)`）。

### 优化层（`SemanticOptimizer`，`--opt`）

- **值流内联**：`T = expr; …use T…`，在「T 只被使用一次 + 定义与使用同属一个块 +
  中间只隔无副作用语句」时把 `expr` 就地代入，并按父运算符优先级决定括号。
- **纯度判定**：`IsPure`/`NoImpureCall` 只放行数学/比较/逻辑/取长度/`IsValid` 等已知纯函数；
  `FetchAllCards(66)` 这类带 out 形参的调用不会被搬动。
- **循环头保护**：绝不把带副作用的赋值搬进循环头（会被重复求值）；
  合法的「循环条件临时量」交给 `FoldGuardPreamble` 用全函数生命周期检查来折叠。
- **同趟互斥**：不会把定义搬进「另一条本身也在被改写的定义行」，否则会留下悬空引用。
- **临时量改名**：`Temp_int_Loop_Counter_Variable` → `loopCounter`，
  `Temp_int_Array_Index_Variable` → `arrayIndex`，`K2Node_DynamicCast_AsBP_Hand_Card` → `asBP_Hand_Card`，
  `K2Node_MakeStruct_LinearColor` → `makeLinearColor` … 先做全函数冲突检查再统一替换。

## 效果（`kards` 导出，34 个资产 / 945 个函数）

| 指标 | 数值 |
|---|---|
| 总行数 | 40,494（`--opt`；不开 `--opt` 为 46,413） |
| 函数签名（来自 `LoadedProperties`） | 945 |
| `goto` / `label` / 悬空 label | **25 / 14 / 0**（原始未消除前为 104 / 50） |
| 守卫 `break` | 1,474 |
| `for` / `while` / `while (true)` | 219 / 60 / 45（全部带 `break`/`return`/`goto` 出口） |
| `(castN)` 未知转换 | 0 |
| 退化渲染（`UAssetAPI.*`） | 0 |
| 残留 `CallFunc_` 名 | 0（未开 `--opt` 时 22,773） |
| 残留 `Temp_*` / `K2Node_*` 名 | 6 / 135（未开 `--opt` 时 7,414 / 5,203） |

主测试用例 `BP_CardFunctions`（292 个函数）：13,834 行，`goto` 20 个，
`for` 173 / `while` 24 / `while (true)` 21（均带出口），`Temp_*` 0 个，`K2Node_*` 19 个。

剩下的 goto 不是没做，而是**不安全**：目标块的直接后支配点与 goto 所在区域的续接点不同，
说明那段尾部后面还接着别的分支（例如紧接着一个 `else`），抄过去会让控制流掉进不该走的分支。
与其猜，不如保真地留一个 goto。

### 效果示例

原字节码（`ExecuteOnCardMovedInHand`）还原结果：

```cpp
void ExecuteOnCardMovedInHand()
{
    BaseCardObject* Array_Get_Item;
    TArray<BaseCardObject*> FetchAllCardsWithEventTrigger_cardsWithThisTrigger;
    bool IsActionProcess_ActionProcess;
    int32 arrayIndex, loopCounter, Add_IntInt_ReturnValue;

    if (IsActionProcess())
    {
        FetchAllCardsWithEventTrigger_cardsWithThisTrigger = FetchAllCardsWithEventTrigger(66);
        for (loopCounter = 0; loopCounter < KismetArrayLibrary.Array_Length(FetchAllCardsWithEventTrigger_cardsWithThisTrigger); loopCounter++)
        {
            arrayIndex = loopCounter;
            Array_Get_Item = KismetArrayLibrary.Array_Get(FetchAllCardsWithEventTrigger_cardsWithThisTrigger, arrayIndex);
            Array_Get_Item.OnOtherCardMovedToLocationInHand();
        }
    }
}
```

## 项目结构

```
KismetDecompiler/
├── KismetDecompiler.csproj         # net10.0 + UAssetAPI 1.1.0 + Newtonsoft.Json + ZstdSharp
├── Program.cs                      # 入口：参数解析、批量、输出装配、ApplyOpt
├── FlowStackResolver.cs            # EX_Push/PopExecutionFlow → 静态跳转
├── StructuredKismetDecompiler.cs   # CFG 结构化 + 反 Dispatch + 文本装配
├── FunctionSignatures.cs           # LoadedProperties → 签名/局部变量/C++ 类型
├── ExprRenderer.cs                 # 表达式 → 伪代码（含运算符还原、常量、名称解析）
├── ReadabilityPasses.cs            # 文本级等价改写（循环还原、goto 消除、括号/取反整理）
├── SemanticOptimizer.cs            # 值流内联、常量折叠、临时量改名
├── PseudoTree.cs                   # 伪代码块树（优化 pass 用）
├── ImportSignatures.cs             # import 路径 → 签名
├── UhtSignatureIndex.cs            # UHT 头文件导出 → 函数签名索引
├── DispatchInliner.cs              # [实验] dispatch 内联回事件函数
├── BytecodeDisassembler.cs         # --disasm / --flowdebug / --cfg 调试视图
└── Decompiled/ .cache/             # 输出（不入库）
```

## 技术要点

- UAssetAPI 已把字节码解析为 `KismetExpression[]`，本工具只做渲染 / 结构 / 优化。
- 执行流栈语义（已实测确认）：`EX_PushExecutionFlow(a)` 压入绝对目标 `a`；
  `EX_PopExecutionFlow()` 无条件跳转；`EX_PopExecutionFlowIfNot(c)` **仅当 `c` 为假时**跳转。
  宏的 `continue`/`break`/作用域退出都走这三个指令，必须先静态解析才能还原循环。
- 后支配树必须加虚拟出口：给所有无后继块补 `succ[i].Add(exit)` 并清空 `succ[exit]`，
  否则整张表会退化成全 1。
- 自然循环的最内层块必须靠 worklist 从回边源点 `u` 反向扩散（`set={v}` 后压 `u`），
  预先把 `u` 放进集合会漏块。
- UE 蓝图里 `float` 在 C++ 侧是 `double`，隐式转换由 `EX_PrimitiveCast` 承担；
  token 3 = double→float，token 4 = float→double（用 `SetApplicationScale(const float)`、
  `MakeVector2D(double,double)`、`GetViewportScale()→float` 等 UHT 签名交叉验证）。
- 蓝图函数名可能含空格（`Update Operable Color`），FName 需要收敛成合法标识符。
- **反 Dispatch 的 case 入口必须当可达性根**：`ExecuteUbergraph` 的每个 case 只能经
  `EX_ComputedJump` 到达，CFG 里没有这条边。只从块 0 做可达性分析会把整个 case 体当死代码删掉，
  `switch` 输出成空壳 —— 实测 `kards` 语料里 1541 个 ubergraph 有 **712 个**中招。
  修法：`SplitBlocks` 之后先把各 `EntryPoint` 对应的块加进 `MarkReachable` 的根集合，再 `Compact`。
- `ImmediateOf` 可以返回**虚拟出口**的下标 `n = _blocks.Count`，它越界于 `_blocks`。
  必须归一成 `-1`：调用方一律把负值当作「直接离开函数」，否则 `_blocks[follow]` 会崩。
- **`IsTempLike` 不要放行 `TempTarget` 认出的全部名字**：循环计数器 / 数组下标也在那张表里，
  而它们的「使用点」本身又是一条赋值（`loopCounter = Add_IntInt_ReturnValue`），
  一旦算作临时量，`defLines.Contains(useLine)` 的保护会连带挡掉 `Add_IntInt_ReturnValue`
  的内联，步进变回两行，`RecoverForLoop` 就再也认不出 `for`（全语料 `for` 从 394 掉到 0）。

## 已知限制

- `--inline` 仍是实验特性：把 `ExecuteUbergraph` 的 case 体复制回每个事件函数，
  产物会膨胀数十倍（BP_BaseCard 约 220 万行），且跨段跳转以注释/goto 保真保留。
  它适合「只看某个事件」的场景，不是默认路径。
- 剩余 `goto`（语料中 25 个）保留：目标块的直接后支配点与 goto 所在区域的续接点不同，
  或尾巴里含 `break/continue`，或尾巴长度超出单函数 900 行的复制预算 —— 条件不成立就不改写。
- 尾递归/`for` 还原不做「循环变量在循环体内被二次赋值」的推断。
- 蓝图里未接线的引脚（如 `Select` 节点的 default、`MakeStruct` 的未赋值成员）会渲染成
  未初始化变量读取 —— 这是原始蓝图的真实状态，不伪造默认值。
