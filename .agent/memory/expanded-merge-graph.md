# 展开的提交图延续

2026-10-11：GitGraphCell 的 PassThrough 原先画 PassLanes + DotLane，遗漏合并提交新增的父泳道，并把已结束的圆点泳道错误延伸到展开区。展开内容位于提交头部下方，应延续头部底端：PassLanes + Links.ToLane 去重。颜色仍取出色，汇入与分叉曲线仍在头部完成。

GitGraphContinuationTests 覆盖合并父泳道、圆点换道/结束、根提交、重复泳道。
