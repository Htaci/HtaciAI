using System.Runtime.CompilerServices;

// 让测试项目能访问 internal 成员。
//
// 用它的理由：有些东西（比如 AttachmentChip 的悬停提示文案）刻意不做成 public ——
// 它是实现细节，不该成为对外的 API 面；但又要能被测试直接断言。
// internal 在这里正是想要的那个可见度。
[assembly: InternalsVisibleTo("HtaciAI.Tests")]
