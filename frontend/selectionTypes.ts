export type TimelineSelection =
    | { kind: "none" }
    | { kind: "clip"; clipIdx: number; stageIdx: number }
    | { kind: "ref"; clipIdx: number; refIdx: number }
    | { kind: "clip-ref"; clipIdx: number; referenceIdx: number }
    | { kind: "ic-lora"; clipIdx: number; entryIdx: number }
    | { kind: "audio"; clipIdx: number }
    | { kind: "audio-track"; trackIdx: number }
    | { kind: "prompt-major"; clipIdx: number }
    | { kind: "prompt-minor"; clipIdx: number; windowIdx: number }
    | { kind: "retake"; clipIdx: number }
    | { kind: "boundary"; leftClipIdx: number };
