"""Join two PoyCoop traces from one PC. Usage: analyze_trace.py <viewer.csv> <mover.csv> <mover_id>
Compares what the viewer SHOWED for the mover against the mover's own TRUE pose at the same sender time."""
import csv, sys, bisect, math, statistics as st
def load(p): return list(csv.DictReader(open(p)))
viewer, mover, mid = load(sys.argv[1]), load(sys.argv[2]), sys.argv[3]
truth = [r for r in mover if r['kind'] == 'self']
tt = [int(r['wall_ms']) for r in truth]
def at(t, k):
    i = bisect.bisect_left(tt, t)
    if i <= 0 or i >= len(tt): return None
    a, b = truth[i-1], truth[i]; ta, tb = tt[i-1], tt[i]
    if tb - ta > 200: return None
    f = (t - ta) / max(1, tb - ta)
    return [float(a[k+c]) + (float(b[k+c]) - float(a[k+c])) * f for c in 'xyz']
def d(p, q): return math.dist(p, q)
shown = [r for r in viewer if r['kind'] == 'shown' and r['id'] == mid]
# phase labels from mover marks
marks = [(int(r['wall_ms']), r['mark']) for r in truth if r['mark'].startswith('puppet:')]
def phase(t):
    lab = 'free'
    for mt, m in marks:
        if mt <= t: lab = m[7:]
    return lab
eb, eh, lat, reach_true, reach_shown = [], [], [], {}, {}
per = {}
for r in shown:
    t = int(r['wall_ms']); loc = int(r['mark'].split('=')[1]) if r['mark'].startswith('local=') else None
    tb, th = at(t, 'b'), at(t, 'l')
    if tb is None: continue
    sb = [float(r['b'+c]) for c in 'xyz']; sh = [float(r['l'+c]) for c in 'xyz']
    e1, e2 = d(sb, tb), d(sh, th); eb.append(e1); eh.append(e2)
    if loc: lat.append(loc - t)
    ph = phase(t); per.setdefault(ph, []).append((e1, e2, d(sh, sb), d(th, tb)))
def q(x, p): x = sorted(x); return x[min(len(x)-1, int(p*len(x)))]
print(f"samples {len(eb)}  (viewer shown rows {len(shown)}, mover truth rows {len(truth)})")
print(f"body error  mean {st.mean(eb)*100:.1f} cm  p95 {q(eb,.95)*100:.1f} cm  max {max(eb)*100:.1f} cm")
print(f"hand error  mean {st.mean(eh)*100:.1f} cm  p95 {q(eh,.95)*100:.1f} cm  max {max(eh)*100:.1f} cm")
if lat: print(f"display delay (local now - sender time of shown pose)  mean {st.mean(lat):.0f} ms  p95 {q(lat,.95):.0f} ms  max {max(lat)} ms")
ages = [int(r['age_ms']) for r in shown]; print(f"newest packet age  mean {st.mean(ages):.0f} ms  max {max(ages)} ms")
print("phase            n   body_err  hand_err  hand-body shown / true (m)")
for ph, v in per.items():
    print(f"{ph:14s} {len(v):4d}  {st.mean(x[0] for x in v)*100:6.1f}cm {st.mean(x[1] for x in v)*100:6.1f}cm   {st.mean(x[2] for x in v):.2f} / {st.mean(x[3] for x in v):.2f}")
