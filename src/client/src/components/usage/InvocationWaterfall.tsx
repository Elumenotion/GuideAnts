import { 
  BarChart, 
  Bar, 
  XAxis, 
  YAxis, 
  Tooltip, 
  ResponsiveContainer,
  Cell
} from 'recharts';
import type { InvocationNodeDto } from '../../types/usage';

interface GuideAntsRow {
  id: string;
  label: string;
  assistantName: string;
  depth: number;
  startOffset: number;
  duration: number;
  tokens: number;
  cost: number;
  isOutlier: boolean;
  status: string;
}

interface __INVOCATION_GUIDEANTS__6d9461c775b14d2c8e06718ed80b2904__Props {
  invocations: InvocationNodeDto[];
  totalDurationMs: number;
  onSelectInvocation: (id: string) => void;
  selectedId?: string;
}

export function __INVOCATION_GUIDEANTS__6d9461c775b14d2c8e06718ed80b2904__({ 
  invocations, 
  totalDurationMs, 
  onSelectInvocation,
  selectedId 
}: __INVOCATION_GUIDEANTS__6d9461c775b14d2c8e06718ed80b2904__Props) {
  
  // Flatten the tree into guideants rows
  const flattenTree = (
    nodes: InvocationNodeDto[], 
    baseOffset: number = 0
  ): GuideAntsRow[] => {
    const rows: GuideAntsRow[] = [];
    
    for (const node of nodes) {
      const indent = '  '.repeat(node.depth) + (node.depth > 0 ? '└─ ' : '');
      
      rows.push({
        id: node.id,
        label: `${indent}${node.assistantName}`,
        assistantName: node.assistantName,
        depth: node.depth,
        startOffset: baseOffset,
        duration: node.durationMs,
        tokens: node.promptTokens + node.completionTokens,
        cost: node.chargeUsd,
        isOutlier: node.isOutlier,
        status: node.status
      });
      
      // Recursively add children
      if (node.children && node.children.length > 0) {
        // Children start after their parent begins
        const childOffset = baseOffset + Math.min(node.durationMs * 0.1, 100);
        const childRows = flattenTree(node.children, childOffset);
        rows.push(...childRows);
      }
    }
    
    return rows;
  };

  const guideantsRows = flattenTree(invocations);
  
  const formatDuration = (ms: number) => {
    if (ms < 1000) return `${ms}ms`;
    if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
    return `${(ms / 60000).toFixed(1)}m`;
  };

  const formatNumber = (value: number) =>
    new Intl.NumberFormat('en-US').format(value);

  const CustomTooltip = ({ active, payload }: any) => {
    if (!active || !payload || !payload.length) return null;

    const data = payload[0]?.payload;
    if (!data) return null;

    return (
      <div className="bg-white p-3 rounded-lg shadow-lg border border-gray-200">
        <div className="text-sm font-medium text-gray-900 mb-2">{data.assistantName}</div>
        <div className="space-y-1 text-sm">
          <div className="flex justify-between gap-4">
            <span className="text-gray-600">Duration:</span>
            <span className="font-medium">{formatDuration(data.duration)}</span>
          </div>
          <div className="flex justify-between gap-4">
            <span className="text-gray-600">Tokens:</span>
            <span className="font-medium">{formatNumber(data.tokens)}</span>
          </div>
          <div className="flex justify-between gap-4">
            <span className="text-gray-600">Status:</span>
            <span className={`font-medium ${
              data.status === 'completed' ? 'text-green-600' :
              data.status === 'failed' ? 'text-red-600' :
              data.status === 'running' ? 'text-blue-600' : 'text-gray-600'
            }`}>
              {data.status}
            </span>
          </div>
          {data.isOutlier && (
            <div className="text-red-600 font-medium pt-1 border-t">
              ⚠️ Outlier
            </div>
          )}
        </div>
      </div>
    );
  };

  if (guideantsRows.length === 0) {
    return (
      <div className="text-sm text-gray-500 text-center py-4">
        No invocations to display
      </div>
    );
  }

  const chartHeight = Math.max(200, guideantsRows.length * 40 + 40);

  return (
    <div className="bg-white">
      <div className="text-sm font-medium text-gray-900 mb-2">
        Execution Timeline ({formatDuration(totalDurationMs)} total)
      </div>
      <div className="text-xs text-gray-500 mb-4 flex items-center gap-4">
        <span className="flex items-center gap-1">
          <span className="w-3 h-3 bg-blue-500 rounded"></span> Normal
        </span>
        <span className="flex items-center gap-1">
          <span className="w-3 h-3 bg-red-500 rounded"></span> Outlier
        </span>
        <span className="flex items-center gap-1">
          <span className="w-3 h-3 bg-blue-300 border-2 border-blue-600 rounded"></span> Selected
        </span>
      </div>
      <ResponsiveContainer width="100%" height={chartHeight}>
        <BarChart 
          layout="vertical" 
          data={guideantsRows}
          margin={{ top: 10, right: 30, left: 10, bottom: 10 }}
        >
          <XAxis 
            type="number" 
            domain={[0, totalDurationMs * 1.1]} 
            tickFormatter={(ms) => formatDuration(ms)}
            tick={{ fontSize: 11 }}
          />
          <YAxis 
            type="category" 
            dataKey="label" 
            width={240} 
            tick={{ fontSize: 11, textAnchor: 'start' as any, dx: -230 }}
            tickLine={false}
          />
          <Tooltip content={<CustomTooltip />} />
          
          {/* Transparent spacer bar for positioning */}
          <Bar dataKey="startOffset" stackId="a" fill="transparent" />
          
          {/* Actual duration bar */}
          <Bar 
            dataKey="duration" 
            stackId="a"
            onClick={(data) => data.id && onSelectInvocation(data.id)}
            style={{ cursor: 'pointer' }}
          >
            {guideantsRows.map((entry, index) => (
              <Cell 
                key={`cell-${index}`} 
                fill={entry.isOutlier ? '#ef4444' : '#3b82f6'}
                stroke={selectedId === entry.id ? '#1d4ed8' : undefined}
                strokeWidth={selectedId === entry.id ? 2 : 0}
                opacity={selectedId && selectedId !== entry.id ? 0.5 : 1}
              />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

