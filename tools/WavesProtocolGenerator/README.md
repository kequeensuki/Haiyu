# WavesProtocolGenerator

.NET 10 开发工具，使用 SDK 自带 Roslyn 语法树，从只读 `DecompiledSource/Aki.Protocol` 恢复 wire schema。

```text
dotnet run --project tools/WavesProtocolGenerator -- --source <Aki.Protocol目录> --output <Haiyu.ABI/Protocol目录> --commit <源码提交> --protoc <protoc可执行文件> [--check]
dotnet run --project tools/WavesProtocolGenerator -- --self-test
```

输出 `WavesGameData.proto`、`waves-protocol.schema.json`、`WavesMessageIds.cs`、`waves-protocol.manifest.json`。生成成功不会改写未变化的文件；`--check` 不写产物，差异 / 失败返回 1，参数缺失返回非零。语法或编码不支持时明确阻断，不降级 / 静默跳过。

模型使用源限定类型名，proto 内确定性扁平命名；枚举值加类型前缀以避免 protobuf 全局枚举值冲突，同时保留 OriginalName 供事件 JSON 使用。8 个不含 0 值的枚举为满足 proto3 语法添加显式命名的合成零值，列表记入版本报告；通用 JSON 对原始未定义枚举数值仍输出数字，不冒充官方枚举名称。读取 codec / InternalMergeFrom / InternalWriteTo 校验编码和 tag。map、oneof、packed、presence、零默认值、依赖及三方 msgId 注册均校验；非零显式默认值暂不支持，会报错。源 BOM / CRLF / LF 规范化后计算 SHA256。

完整操作、运行架构、限制和 Windows 验收见 `Dev/Development/全协议事件导出-实施与Windows验收.md`。
