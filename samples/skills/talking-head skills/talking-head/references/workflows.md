# Workflows (talking-head i2v)

| Workflow id | Skill | Use |
|-------------|-------|-----|
| `infinitetalk-i2v-v1` | `talking-head-i2v` | Avatar + audio + background → composited MP4 |

V2V (`infinitetalk-v2v-v1`) may exist on the adapter host for host harnesses; it is
**not** a skill surface. Do not route agents to V2V from this pack.

Skills must not mutate workflow JSON on disk.

## Seeing them in ComfyUI

On the GPU host, open ComfyUI and use **Workflows**:

| Folder | Contents |
|--------|----------|
| `guideants/` | Template graphs (UI format; placeholders) |
| `guideants-jobs/` | Exact **rendered** graph for each submitted job (`{workflow}__{jobId}.json`) plus `_RUNNING__{workflow}.json` |

Load a `guideants-jobs/*.json` file (UI format) to inspect the graph that is / was running.

## Job shape

One adapter job owns generate + CorridorKey composite. Client submits once, polls
`/v1/talking-head/jobs/{id}`, then downloads `/result` as MP4.
