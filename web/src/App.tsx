import { useCallback, useEffect, useMemo, useState, type FormEvent, type ReactNode } from 'react'
import {
  Activity, AlertTriangle, ArrowDownRight, ArrowUpRight, ArrowLeft, Bell, Check, CheckCircle2,
  ChevronDown, CircleHelp, Clock3, Cpu, FileClock, Fingerprint, Gauge, LayoutDashboard, ClipboardList,
  LogOut, LockKeyhole, RefreshCw, Search, ShieldAlert, ShieldCheck,
  Sparkles, UserRound, X, Zap,
} from 'lucide-react'
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import {
  Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts'

type Auth = { token: string; email: string; roles: string[]; expiresAtUtc: string }
type Transaction = {
  id: string; externalId: string; amount: number; occurredAt: string; riskScore: number;
  isFlagged: boolean; reviewStatus: string; modelVersion: string; createdAt: string;
  explanations?: Explanation[]; explanationMethod?: string
}
type TxPage = { items: Transaction[]; page: number; pageSize: number; total: number }
type Summary = {
  totalScored: number; flagged: number; pendingReview: number; reviewed: number; averageRisk: number;
  activeModelVersion?: string | null; prAuc?: number | null; precision?: number | null; recall?: number | null; threshold?: number | null; modelLoaded: boolean
}
type Health = {
  modelLoaded: boolean; activeVersion?: string | null; threshold?: number | null; prAuc?: number | null; precision?: number | null; recall?: number | null;
  scoredLast24Hours: number; meanAbsoluteFeatureShift?: number | null; maxAbsoluteFeatureShift?: number | null; driftAlert: boolean;
  driftMethod: string; datasetStatus: string; modelCreatedAt?: string | null
}
type Explanation = { name: string; probabilityDelta: number | null }
type ScoringResponse = { transaction: Transaction; probability: number; threshold?: number; explanations: Explanation[]; explanationMethod: string }
type ModelRow = { version: string; isActive: boolean; prAuc: number; precision: number; recall: number; threshold: number; trainCount: number; validationCount: number; testCount: number; fraudTestCount: number; createdAt: string }
type SectionId = 'overview' | 'review-queue' | 'scorer' | 'models' | 'evaluation' | 'audit-trail' | 'model-health'
type BinaryMetrics = { threshold: number; precision: number; recall: number; f1: number; accuracy: number; falsePositiveRate: number; truePositive: number; falsePositive: number; trueNegative: number; falseNegative: number; predictedPositive: number; total: number }
type ThresholdPoint = { threshold: number; precision: number; recall: number; f1: number; predictedPositive: number }
type Evaluation = { version: string; evaluatedAt: string; validationCount: number; validationFraudCount: number; testCount: number; testFraudCount: number; prAuc: number; testMetrics: BinaryMetrics; validationThresholdAnalysis: ThresholdPoint[]; thresholdMethod: string; testSetCaveat: string }
type ReviewAudit = { id: number; transactionId: string; externalId: string; amount: number; riskScore: number; modelVersion: string; isFraud: boolean; reviewerEmail: string; note?: string | null; createdAt: string }
type MonitoringBucket = { window: string; scored: number; flagged: number; averageRisk: number }
type Monitoring = { generatedAt: string; scoredLast24Hours: number; flaggedLast24Hours: number; pendingReview: number; reviewedLast24Hours: number; flagRate: number; lowRiskCount: number; mediumRiskCount: number; highRiskCount: number; timeline: MonitoringBucket[]; timelineNote: string; driftAlert: boolean; meanFeatureShift?: number | null; maxFeatureShift?: number | null; driftMethod: string }

const sectionTargets: Record<SectionId, string> = {
  overview: 'overview', 'review-queue': 'review-queue', scorer: 'scorer', models: 'models',
  evaluation: 'evaluation-lab', 'audit-trail': 'audit-trail', 'model-health': 'monitoring-detail',
}
const sectionLabels: Record<SectionId, string> = {
  overview: 'Overview', 'review-queue': 'Review queue', scorer: 'Live scoring', models: 'Model registry',
  evaluation: 'Evaluation lab', 'audit-trail': 'Audit trail', 'model-health': 'Model health',
}
function sectionFromHash(hash: string): SectionId {
  const value = hash.replace(/^#/, '') as SectionId
  return Object.prototype.hasOwnProperty.call(sectionTargets, value) ? value : 'overview'
}

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? ''
const initialFeatures = JSON.stringify(Array.from({ length: 28 }, () => 0), null, 2)

// ASP.NET Core normally serializes API contracts as camelCase. Normalize anyway so
// alternate serializer settings or stale cached responses cannot crash the UI.
function normalizeJson<T>(value: unknown): T {
  if (Array.isArray(value)) return value.map((item) => normalizeJson(item)) as T
  if (value && typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>).map(([key, item]) => [
      key.length ? key[0].toLowerCase() + key.slice(1) : key,
      normalizeJson(item),
    ])
    return Object.fromEntries(entries) as T
  }
  return value as T
}

function readStoredAuth(): Auth | null {
  try {
    const stored = normalizeJson<Partial<Auth> | null>(JSON.parse(sessionStorage.getItem('sentinel-auth') ?? 'null'))
    if (!stored || typeof stored.token !== 'string' || !stored.token) {
      sessionStorage.removeItem('sentinel-auth')
      return null
    }
    return {
      token: stored.token,
      email: typeof stored.email === 'string' ? stored.email : 'analyst',
      roles: Array.isArray(stored.roles) ? stored.roles.filter((role): role is string => typeof role === 'string') : [],
      expiresAtUtc: typeof stored.expiresAtUtc === 'string' ? stored.expiresAtUtc : '',
    }
  } catch {
    sessionStorage.removeItem('sentinel-auth')
    return null
  }
}

async function api<T>(path: string, token: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${token}`,
      ...(init.headers ?? {}),
    },
  })
  if (!response.ok) {
    const body = normalizeJson<{ detail?: string; message?: string; title?: string } | null>(await response.json().catch(() => null))
    throw new Error(body?.detail ?? body?.message ?? body?.title ?? `Request failed (${response.status})`)
  }
  if (response.status === 204) return undefined as T
  return normalizeJson<T>(await response.json())
}

function formatPct(value?: number | null, digits = 1) {
  return value === undefined || value === null || !Number.isFinite(value) ? 'N/A' : `${(value * 100).toFixed(digits)}%`
}
// API metrics can intentionally be null before a model has been trained or enough
// scored transactions exist. Render a neutral placeholder instead of calling toFixed on null.
function formatFixed(value: number | null | undefined, digits = 2, suffix = '') {
  return value === undefined || value === null || !Number.isFinite(value) ? 'N/A' : `${value.toFixed(digits)}${suffix}`
}
function formatDeltaPp(value: number | null | undefined) {
  if (value === undefined || value === null || !Number.isFinite(value)) return 'N/A'
  const points = value * 100
  return `${points >= 0 ? '+' : ''}${points.toFixed(2)} pp`
}
function formatMoney(value: number) {
  return new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 }).format(value)
}
function shortTime(value: string) {
  return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}
function shortDate(value: string) {
  return new Date(value).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}
function statusLabel(value: string) {
  return value === 'ConfirmedFraud' ? 'Fraud confirmed' : value === 'Legitimate' ? 'Cleared' : value === 'NotFlagged' ? 'Not flagged' : 'Needs review'
}

export default function App() {
  const [auth, setAuth] = useState<Auth | null>(readStoredAuth)
  const [registerMode, setRegisterMode] = useState(false)
  const [email, setEmail] = useState('admin-recovery@local.dev')
  const [password, setPassword] = useState('')
  const [authError, setAuthError] = useState('')
  const [busy, setBusy] = useState(false)
  const [summary, setSummary] = useState<Summary | null>(null)
  const [health, setHealth] = useState<Health | null>(null)
  const [queue, setQueue] = useState<Transaction[]>([])
  const [allModels, setAllModels] = useState<ModelRow[]>([])
  const [evaluation, setEvaluation] = useState<Evaluation | null>(null)
  const [evaluationLoading, setEvaluationLoading] = useState(false)
  const [evaluationError, setEvaluationError] = useState('')
  const [auditRows, setAuditRows] = useState<ReviewAudit[]>([])
  const [auditLoading, setAuditLoading] = useState(false)
  const [auditError, setAuditError] = useState('')
  const [monitoring, setMonitoring] = useState<Monitoring | null>(null)
  const [filter, setFilter] = useState('Pending')
  const [searchQuery, setSearchQuery] = useState('')
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [connection, setConnection] = useState<'connecting' | 'live' | 'offline'>('connecting')
  const [reviewing, setReviewing] = useState<Transaction | null>(null)
  const [reviewNote, setReviewNote] = useState('')
  const [selectedVerdict, setSelectedVerdict] = useState<boolean | null>(null)
  const [thresholdDraft, setThresholdDraft] = useState(0.5)
  const [amount, setAmount] = useState('2499')
  const [timeValue, setTimeValue] = useState('50000')
  const [features, setFeatures] = useState(initialFeatures)
  const [scoreBusy, setScoreBusy] = useState(false)
  const [scoreResult, setScoreResult] = useState<ScoringResponse | null>(null)
  const [showScorer, setShowScorer] = useState(false)
  const [showModels, setShowModels] = useState(false)
  const [showEvaluation, setShowEvaluation] = useState(false)
  const [showAudit, setShowAudit] = useState(false)
  const [activeSection, setActiveSection] = useState<SectionId>(() => sectionFromHash(window.location.hash))
  const [sectionHistory, setSectionHistory] = useState<SectionId[]>([])

  const isAdmin = auth?.roles?.some((role) => role.toLowerCase() === 'admin') ?? false

  const logout = useCallback(() => {
    sessionStorage.removeItem('sentinel-auth')
    window.history.replaceState({}, '', `${window.location.pathname}${window.location.search}`)
    setAuth(null)
    setSummary(null)
    setHealth(null)
    setQueue([])
    setAllModels([])
    setEvaluation(null)
    setAuditRows([])
    setMonitoring(null)
    setSectionHistory([])
    setActiveSection('overview')
  }, [])

  const loadDashboard = useCallback(async () => {
    if (!auth) return
    try {
      const [nextSummary, nextHealth, nextPage, models, nextMonitoring] = await Promise.all([
        api<Summary>('/api/transactions/summary', auth.token),
        api<Health>('/api/models/health', auth.token),
        api<TxPage>(`/api/transactions?status=${encodeURIComponent(filter)}&page=1&pageSize=100`, auth.token),
        api<ModelRow[]>('/api/models', auth.token),
        api<Monitoring>('/api/models/monitoring', auth.token),
      ])
      setSummary(nextSummary)
      setHealth(nextHealth)
      setQueue(Array.isArray(nextPage?.items) ? nextPage.items : [])
      setAllModels(Array.isArray(models) ? models : [])
      setMonitoring(nextMonitoring)
      setThresholdDraft(nextHealth.threshold ?? 0.5)
      setError('')
    } catch (e) {
      const message = e instanceof Error ? e.message : 'Could not load dashboard data.'
      if (message.toLowerCase().includes('401') || message.toLowerCase().includes('unauthorized')) logout()
      else setError(message)
    }
  }, [auth, filter, logout])

  useEffect(() => { void loadDashboard() }, [loadDashboard])

  async function loadEvaluation() {
    if (!auth) return
    setEvaluationLoading(true); setEvaluationError('')
    try { setEvaluation(await api<Evaluation>('/api/models/evaluation', auth.token)) }
    catch (e) { setEvaluationError(e instanceof Error ? e.message : 'Could not evaluate the active model.') }
    finally { setEvaluationLoading(false) }
  }

  async function loadAudit() {
    if (!auth) return
    setAuditLoading(true); setAuditError('')
    try {
      const rows = await api<ReviewAudit[]>('/api/transactions/reviews/recent?pageSize=50', auth.token)
      setAuditRows(Array.isArray(rows) ? rows : [])
    } catch (e) { setAuditError(e instanceof Error ? e.message : 'Could not load analyst audit history.') }
    finally { setAuditLoading(false) }
  }

  function navigateToSection(section: SectionId) {
    if (section !== activeSection) {
      setSectionHistory((previous) => [...previous, activeSection].slice(-40))
      window.history.pushState({ sentinelSection: section }, '', `#${section}`)
      setActiveSection(section)
    }
    if (section === 'review-queue') setFilter('Pending')
    if (section === 'scorer') setShowScorer(true)
    if (section === 'models') setShowModels(true)
    if (section === 'evaluation') setShowEvaluation(true)
    if (section === 'audit-trail') setShowAudit(true)
    window.setTimeout(() => document.getElementById(sectionTargets[section])?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 90)
  }

  function goBack() {
    if (sectionHistory.length > 0) window.history.back()
    else if (activeSection !== 'overview') navigateToSection('overview')
  }

  useEffect(() => {
    const onPopState = () => {
      const section = sectionFromHash(window.location.hash)
      setActiveSection(section)
      setSectionHistory((previous) => previous.slice(0, -1))
      if (section === 'review-queue') setFilter('Pending')
      if (section === 'scorer') setShowScorer(true)
      if (section === 'models') setShowModels(true)
      if (section === 'evaluation') setShowEvaluation(true)
      if (section === 'audit-trail') setShowAudit(true)
      window.setTimeout(() => document.getElementById(sectionTargets[section])?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 90)
    }
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [auth])

  useEffect(() => {
    if (!auth) return
    if (activeSection === 'review-queue') setFilter('Pending')
    if (activeSection === 'scorer') setShowScorer(true)
    if (activeSection === 'models') setShowModels(true)
    if (activeSection === 'evaluation') { setShowEvaluation(true); void loadEvaluation() }
    if (activeSection === 'audit-trail') { setShowAudit(true); void loadAudit() }
    const timer = window.setTimeout(() => document.getElementById(sectionTargets[activeSection])?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 100)
    return () => window.clearTimeout(timer)
  }, [activeSection, auth])

  useEffect(() => {
    if (!auth) return
    let current: HubConnection | null = null
    let stopped = false
    const connect = async () => {
      current = new HubConnectionBuilder()
        .withUrl(`${API_BASE}/hubs/transactions`, { accessTokenFactory: () => auth.token })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build()
      current.on('transactionFlagged', () => {
        setNotice('A new high-risk transaction entered the review queue.')
        void loadDashboard()
      })
      current.on('transactionReviewed', () => { void loadDashboard() })
      current.onreconnecting(() => setConnection('connecting'))
      current.onreconnected(() => setConnection('live'))
      current.onclose(() => setConnection('offline'))
      try {
        await current.start()
        if (!stopped) setConnection('live')
      } catch {
        if (!stopped) setConnection('offline')
      }
    }
    void connect()
    return () => {
      stopped = true
      if (current) void current.stop()
    }
  }, [auth, loadDashboard])

  async function handleAuth(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setBusy(true); setAuthError('')
    try {
      const result = await fetch(`${API_BASE}/api/auth/${registerMode ? 'register' : 'login'}`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }),
      })
      const body = normalizeJson<Auth | { message?: string; detail?: string; title?: string; errors?: Record<string, string[]> } | null>(await result.json().catch(() => null))
      if (!result.ok) {
        const obj = body as { message?: string; detail?: string; title?: string; errors?: Record<string, string[]> } | null
        const errorText = obj?.message ?? obj?.detail ?? obj?.title ?? Object.values(obj?.errors ?? {}).flat().join(' ') ?? 'Authentication failed.'
        throw new Error(errorText || 'Authentication failed.')
      }
      const candidate = body as Partial<Auth> | null
      if (!candidate || typeof candidate.token !== 'string' || !candidate.token || typeof candidate.email !== 'string') {
        throw new Error('The API response did not include a valid token and email. Check the API container logs and rebuild the latest frontend.')
      }
      const nextAuth: Auth = {
        token: candidate.token,
        email: candidate.email,
        roles: Array.isArray(candidate.roles) ? candidate.roles.filter((role): role is string => typeof role === 'string') : [],
        expiresAtUtc: typeof candidate.expiresAtUtc === 'string' ? candidate.expiresAtUtc : '',
      }
      sessionStorage.setItem('sentinel-auth', JSON.stringify(nextAuth))
      setAuth(nextAuth)
      setPassword('')
      setError('')
      setNotice(registerMode ? 'Analyst account created.' : 'Welcome back.')
    } catch (e) {
      setAuthError(e instanceof Error ? e.message : 'Could not sign in.')
    } finally { setBusy(false) }
  }

  async function runScore(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!auth) return
    setScoreBusy(true); setError(''); setScoreResult(null)
    try {
      const vector = JSON.parse(features) as unknown
      if (!Array.isArray(vector) || vector.length !== 28 || vector.some((x) => typeof x !== 'number' || !Number.isFinite(x)))
        throw new Error('V must be a JSON array of exactly 28 finite numbers.')
      const result = await api<ScoringResponse>('/api/transactions/score', auth.token, {
        method: 'POST', body: JSON.stringify({ amount: Number(amount), time: Number(timeValue), v: vector }),
      })
      setScoreResult(result)
      setNotice(result.transaction.isFlagged ? 'Transaction flagged and added to the review queue.' : 'Transaction scored below the current review threshold.')
      await loadDashboard()
    } catch (e) { setError(e instanceof Error ? e.message : 'Scoring request failed.') }
    finally { setScoreBusy(false) }
  }

  async function submitReview() {
    if (!auth || !reviewing || selectedVerdict === null) return
    setBusy(true); setError('')
    try {
      await api(`/api/transactions/${reviewing.id}/review`, auth.token, {
        method: 'POST', body: JSON.stringify({ isFraud: selectedVerdict, note: reviewNote }),
      })
      setNotice(selectedVerdict ? 'Confirmed fraud. Label added to the next training run.' : 'Marked legitimate. Label added to the next training run.')
      setReviewing(null); setReviewNote(''); setSelectedVerdict(null)
      await loadDashboard()
    } catch (e) { setError(e instanceof Error ? e.message : 'Review could not be saved.') }
    finally { setBusy(false) }
  }

  async function changeThreshold() {
    if (!auth) return
    setBusy(true); setError('')
    try {
      await api('/api/models/threshold', auth.token, { method: 'PUT', body: JSON.stringify({ threshold: thresholdDraft }) })
      setNotice(`Decision threshold updated to ${formatPct(thresholdDraft)}.`)
      await loadDashboard()
    } catch (e) { setError(e instanceof Error ? e.message : 'Threshold update failed.') }
    finally { setBusy(false) }
  }

  async function retrain() {
    if (!auth) return
    setBusy(true); setError('')
    try {
      const next = await api<ModelRow>('/api/models/retrain', auth.token, { method: 'POST' })
      setNotice(`Model ${next.version} trained. PR-AUC ${formatPct(next.prAuc)} on a chronological holdout.`)
      await loadDashboard()
    } catch (e) { setError(e instanceof Error ? e.message : 'Retraining failed.') }
    finally { setBusy(false) }
  }

  const filteredQueue = useMemo(() => queue.filter((item) => {
    const search = searchQuery.trim().toLowerCase()
    return !search || item.externalId.toLowerCase().includes(search) || item.id.toLowerCase().includes(search)
  }), [queue, searchQuery])

  const chartData = useMemo(() => [...queue].reverse().slice(-24).map((item) => ({
    time: shortTime(item.createdAt), risk: Number.isFinite(item.riskScore) ? Number((item.riskScore * 100).toFixed(2)) : null,
  })), [queue])

  if (!auth) return <LoginScreen email={email} setEmail={setEmail} password={password} setPassword={setPassword}
    registerMode={registerMode} setRegisterMode={setRegisterMode} onSubmit={handleAuth} busy={busy} error={authError} />

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand-lockup"><div className="brand-mark"><ShieldCheck size={22} /></div><div><span className="brand-name">SENTINEL</span><span className="brand-subtitle">FRAUD INTELLIGENCE</span></div></div>
        <div className="workspace-switch"><div className="workspace-icon">R</div><div className="workspace-copy"><strong>Risk Operations</strong><span>Local demo environment</span></div><ChevronDown size={15} /></div>
        <div className="nav-label">WORKSPACE</div>
        <nav className="nav-items" aria-label="Sentinel sections">
          <button className={`nav-item ${activeSection === 'overview' ? 'active' : ''}`} onClick={() => navigateToSection('overview')}><LayoutDashboard size={17} /> Overview {activeSection === 'overview' ? <span className="nav-indicator" /> : null}</button>
          <button className={`nav-item ${activeSection === 'review-queue' ? 'active' : ''}`} onClick={() => navigateToSection('review-queue')}><ShieldAlert size={17} /> Review queue {summary?.pendingReview ? <span className="nav-count">{summary.pendingReview}</span> : null}</button>
          <button className={`nav-item ${activeSection === 'scorer' ? 'active' : ''}`} onClick={() => navigateToSection('scorer')}><Zap size={17} /> Live scoring</button>
          <button className={`nav-item ${activeSection === 'models' ? 'active' : ''}`} onClick={() => navigateToSection('models')}><Cpu size={17} /> Model registry</button>
          <button className={`nav-item ${activeSection === 'evaluation' ? 'active' : ''}`} onClick={() => navigateToSection('evaluation')}><Gauge size={17} /> Evaluation lab</button>
          <button className={`nav-item ${activeSection === 'audit-trail' ? 'active' : ''}`} onClick={() => navigateToSection('audit-trail')}><ClipboardList size={17} /> Audit trail</button>
          <button className={`nav-item ${activeSection === 'model-health' ? 'active' : ''}`} onClick={() => navigateToSection('model-health')}><Activity size={17} /> Model health <span className={`health-dot ${health?.driftAlert ? 'warning' : health?.modelLoaded ? 'good' : ''}`} /></button>
        </nav>
        <div className="sidebar-bottom">
          <div className="sidebar-help"><div className="help-icon"><CircleHelp size={17} /></div><div><strong>Need a hand?</strong><span>Read the runbook</span></div><ArrowUpRight size={14} /></div>
          <div className="user-card"><div className="avatar"><UserRound size={17} /></div><div className="user-copy"><strong>{auth.email}</strong><span>{isAdmin ? 'Administrator' : 'Risk analyst'}</span></div><button className="icon-button logout-button" onClick={logout} title="Sign out"><LogOut size={16} /></button></div>
        </div>
      </aside>

      <main className="main-area">
        <header className="topbar">
          <div className="breadcrumbs"><button className="back-to-section" onClick={goBack} disabled={sectionHistory.length === 0 && activeSection === 'overview'} aria-label="Go back to previous section"><ArrowLeft size={14} /><span>Back</span></button><span>Workspace</span><span className="crumb-slash">/</span><strong>{sectionLabels[activeSection]}</strong></div>
          <div className="topbar-right"><span className={`live-indicator ${connection}`}><span />{connection === 'live' ? 'Live updates on' : connection === 'connecting' ? 'Connecting' : 'Live feed offline'}</span><button className="icon-button topbar-bell" title="Notifications" onClick={() => setNotice('Notifications are delivered to this console while it is open.')}><Bell size={18} /><i /></button><div className="topbar-avatar">{(auth.email || 'A').slice(0, 1).toUpperCase()}</div></div>
        </header>

        <div className="page-content">
          <section className="page-heading" id="overview"><div><div className="eyebrow"><span className="eyebrow-line" /> RISK OPERATIONS / OVERVIEW</div><h1>Fraud intelligence <span>at a glance.</span></h1><p>Real-time risk signals, analyst decisions, and model performance in one place.</p></div><div className="heading-actions"><button className="button button-secondary" onClick={() => void loadDashboard()} disabled={busy}><RefreshCw size={15} /> Refresh</button><button className="button button-primary" onClick={() => navigateToSection('scorer')}><Zap size={15} /> Score transaction</button></div></section>

          {error ? <div className="banner banner-error"><AlertTriangle size={17} /><span>{error}</span><button onClick={() => setError('')}><X size={15} /></button></div> : null}
          {notice ? <div className="banner banner-success"><CheckCircle2 size={17} /><span>{notice}</span><button onClick={() => setNotice('')}><X size={15} /></button></div> : null}

          <section className="stat-grid">
            <StatCard label="TRANSACTIONS SCORED" value={summary?.totalScored?.toLocaleString() ?? '—'} note="All-time processed" icon={<Activity size={18} />} tone="cyan" trend="tracked" />
            <StatCard label="FLAGGED TRANSACTIONS" value={summary?.flagged?.toLocaleString() ?? '—'} note="Crossed active threshold" icon={<ShieldAlert size={18} />} tone="orange" trend={summary ? `${formatPct(summary.totalScored ? summary.flagged / summary.totalScored : 0)} of volume` : 'Waiting for data'} />
            <StatCard label="AWAITING REVIEW" value={summary?.pendingReview?.toLocaleString() ?? '—'} note="Requires analyst decision" icon={<Clock3 size={18} />} tone="violet" trend={summary?.pendingReview ? 'Action needed' : 'Queue clear'} />
            <StatCard label="MODEL PR-AUC" value={formatPct(summary?.prAuc)} note="Chronological test holdout" icon={<Gauge size={18} />} tone="green" trend={summary?.modelLoaded ? `v${summary.activeModelVersion?.slice(0, 8) ?? 'active'}` : 'No model loaded'} />
          </section>

          <section className="content-grid">
            <div className="panel risk-panel">
              <div className="panel-heading"><div><div className="section-kicker">SIGNAL ACTIVITY</div><h2>Risk score stream</h2><p>Latest scored transactions · risk probability</p></div><span className="chart-range"><span /> Live window</span></div>
              {chartData.length ? <div className="chart-wrap"><ResponsiveContainer width="100%" height="100%"><AreaChart data={chartData} margin={{ top: 12, right: 10, left: -18, bottom: 0 }}><defs><linearGradient id="riskFill" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="#59d8d0" stopOpacity={0.23} /><stop offset="95%" stopColor="#59d8d0" stopOpacity={0.01} /></linearGradient></defs><CartesianGrid stroke="#26313e" strokeDasharray="4 5" vertical={false} /><XAxis dataKey="time" tick={{ fill: '#8390a1', fontSize: 10 }} tickLine={false} axisLine={false} minTickGap={24} /><YAxis domain={[0, 100]} tick={{ fill: '#8390a1', fontSize: 10 }} tickLine={false} axisLine={false} tickFormatter={(v) => `${v}%`} /><Tooltip contentStyle={{ background: '#141d28', border: '1px solid #2c3947', borderRadius: 8, color: '#f3f7fb', fontSize: 12 }} formatter={(value) => [`${value}%`, 'Risk']} labelStyle={{ color: '#aab5c3' }} /><Area type="monotone" dataKey="risk" stroke="#59d8d0" strokeWidth={2} fill="url(#riskFill)" activeDot={{ r: 4, fill: '#59d8d0', stroke: '#17212c', strokeWidth: 2 }} /></AreaChart></ResponsiveContainer></div> : <EmptyChart modelLoaded={Boolean(health?.modelLoaded)} />}
              <div className="chart-footer"><span><i className="legend-dot" /> Risk probability</span><span>{chartData.length} visible samples</span><span className="footer-note">Not a calibrated loss estimate</span></div>
            </div>

            <div className="panel model-panel" id="model-health">
              <div className="panel-heading"><div><div className="section-kicker">MODEL OBSERVABILITY</div><h2>Model health</h2><p>Active model status and data-shift screening</p></div><div className={`model-status ${health?.modelLoaded ? 'status-online' : 'status-offline'}`}><span />{health?.modelLoaded ? 'Online' : 'Not trained'}</div></div>
              <div className="model-version-row"><div className="model-logo"><Sparkles size={20} /></div><div className="model-version-copy"><strong>{health?.activeVersion ?? 'No active model'}</strong><span>{health?.modelCreatedAt ? `Created ${shortDate(health.modelCreatedAt)}` : 'Train with the ULB dataset to activate scoring'}</span></div><span className="version-tag">ML.NET</span></div>
              <div className="model-metrics"><Metric label="PR-AUC" value={formatPct(health?.prAuc)} detail="test split" /><Metric label="Precision" value={formatPct(health?.precision)} detail="training snapshot" /><Metric label="Recall" value={formatPct(health?.recall)} detail="training snapshot" /></div>
              <div className="health-divider" />
              <div className="health-row"><span>Scored in last 24h</span><strong>{health?.scoredLast24Hours ?? '—'}</strong></div>
              <div className="health-row"><span>Mean absolute feature shift</span><strong>{formatFixed(health?.meanAbsoluteFeatureShift, 2, ' z')}</strong></div>
              <div className="health-row"><span>Max feature shift</span><strong className={health?.driftAlert ? 'text-warning' : ''}>{formatFixed(health?.maxAbsoluteFeatureShift, 2, ' z')}</strong></div>
              <div className={`drift-callout ${health?.driftAlert ? 'drift-alert' : health?.modelLoaded ? 'drift-ok' : ''}`}><span className="drift-icon">{health?.driftAlert ? <AlertTriangle size={16} /> : <Activity size={16} />}</span><div><strong>{health?.driftAlert ? 'Feature shift needs attention' : health?.modelLoaded ? 'No high-shift signal detected' : 'Monitoring not initialized'}</strong><p>{health?.driftAlert ? 'At least one feature mean is 3+ training standard deviations away.' : 'The screen compares recent feature means with training-set statistics.'}</p></div></div>
              <div className="panel-disclaimer">A screening indicator, not a formal multivariate drift test.</div>
            </div>
          </section>

          {showScorer ? <section className="panel scorer-panel" id="scorer"><div className="panel-heading"><div><div className="section-kicker">INFERENCE API</div><h2>Score a transaction</h2><p>Input format matches the public ULB dataset. V1–V28 are anonymized PCA features.</p></div><button className="icon-button" onClick={() => { setShowScorer(false); if (activeSection === 'scorer') navigateToSection('overview') }} aria-label="Close scorer"><X size={18} /></button></div><form className="scorer-form" onSubmit={runScore}><label>Amount (dataset currency)<input value={amount} onChange={(e) => setAmount(e.target.value)} type="number" min="0" step="0.01" required /></label><label>Time feature (seconds from dataset start)<input value={timeValue} onChange={(e) => setTimeValue(e.target.value)} type="number" min="0" step="1" required /></label><label className="feature-input">V1–V28 vector <textarea value={features} onChange={(e) => setFeatures(e.target.value)} spellCheck={false} rows={4} required /></label><div className="scorer-actions"><span>Feature values are required for meaningful scoring. Zeroes are a syntax example, not a realistic transaction.</span><button className="button button-primary" type="submit" disabled={scoreBusy || !health?.modelLoaded}>{scoreBusy ? <RefreshCw className="spin" size={15} /> : <Zap size={15} />}{scoreBusy ? 'Scoring…' : 'Score transaction'}</button></div></form>{scoreResult ? <div className="score-result"><div className={`score-result-meter ${scoreResult.transaction.isFlagged ? 'is-risk' : ''}`}><span>RISK PROBABILITY</span><strong>{formatPct(scoreResult.probability, 2)}</strong><small>{scoreResult.transaction.isFlagged ? 'Flagged for analyst review' : 'Below review threshold'}</small></div><div className="score-explanation"><strong>Highest feature sensitivities</strong><p>{scoreResult.explanationMethod}</p>{scoreResult.explanations.map((ex) => <div className="explanation-row" key={ex.name}><span>{ex.name}</span><span className={ex.probabilityDelta === null || !Number.isFinite(ex.probabilityDelta) ? '' : ex.probabilityDelta >= 0 ? 'text-warning' : 'text-good'}>{formatDeltaPp(ex.probabilityDelta)}</span></div>)}</div></div> : null}</section> : null}

          <section className="panel queue-panel" id="review-queue">
            <div className="panel-heading queue-heading"><div><div className="section-kicker">ANALYST WORKBENCH</div><h2>Transaction review queue</h2><p>Review flagged cases and feed confirmed outcomes into future training.</p></div><button className="button button-secondary" onClick={() => void loadDashboard()}><RefreshCw size={14} /> Sync queue</button></div>
            <div className="table-toolbar"><div className="filter-tabs"><button className={filter === 'Pending' ? 'selected' : ''} onClick={() => setFilter('Pending')}>Needs review <span>{summary?.pendingReview ?? 0}</span></button><button className={filter === 'ConfirmedFraud' ? 'selected' : ''} onClick={() => setFilter('ConfirmedFraud')}>Confirmed fraud</button><button className={filter === 'Legitimate' ? 'selected' : ''} onClick={() => setFilter('Legitimate')}>Cleared</button><button className={filter === 'all' ? 'selected' : ''} onClick={() => setFilter('all')}>All scored</button></div><div className="table-search"><Search size={15} /><input value={searchQuery} onChange={(e) => setSearchQuery(e.target.value)} placeholder="Search transaction ID" /></div></div>
            <div className="table-scroll"><table><thead><tr><th>TRANSACTION</th><th>AMOUNT</th><th>RISK SCORE</th><th>STATUS</th><th>MODEL VERSION</th><th>TIME</th><th /></tr></thead><tbody>{filteredQueue.map((item) => <tr key={item.id}><td><div className="transaction-cell"><span className={`tx-icon ${item.isFlagged ? 'tx-high' : 'tx-low'}`}>{item.isFlagged ? <ShieldAlert size={15} /> : <ShieldCheck size={15} />}</span><div><strong>{item.externalId.length > 18 ? `${item.externalId.slice(0, 8)}…${item.externalId.slice(-5)}` : item.externalId}</strong><span>{item.id.slice(0, 8).toUpperCase()}</span></div></div></td><td className="amount-cell">{formatMoney(item.amount)}</td><td><div className="risk-cell"><strong className={item.riskScore >= (health?.threshold ?? 0.5) ? 'text-warning' : ''}>{formatPct(item.riskScore, 2)}</strong><span className="risk-track"><i style={{ width: `${Math.max(2, Math.min(100, item.riskScore * 100))}%` }} /></span></div></td><td><span className={`status-pill ${item.reviewStatus === 'Pending' ? 'pill-pending' : item.reviewStatus === 'ConfirmedFraud' ? 'pill-danger' : item.reviewStatus === 'Legitimate' ? 'pill-cleared' : 'pill-muted'}`}><i />{statusLabel(item.reviewStatus)}</span></td><td><span className="mono-version">{item.modelVersion.slice(0, 13)}</span></td><td><span className="muted-time">{shortDate(item.createdAt)}</span></td><td>{item.reviewStatus === 'Pending' ? <button className="button button-small" onClick={() => { setReviewing(item); setSelectedVerdict(null); setReviewNote('') }}>Review <ArrowUpRight size={13} /></button> : <span className="row-done"><Check size={15} /> Done</span>}</td></tr>)}</tbody></table></div>
            {!filteredQueue.length ? <div className="empty-queue"><div className="empty-queue-icon"><FileClock size={21} /></div><strong>{filter === 'Pending' ? 'Nothing waiting for review' : 'No transactions in this view'}</strong><span>{health?.modelLoaded ? 'Score a transaction or change the status filter to see activity.' : 'Train a model first, then score transactions to populate the queue.'}</span><button className="button button-secondary" onClick={() => { setShowScorer(true); document.getElementById('scorer')?.scrollIntoView({ behavior: 'smooth' }) }} disabled={!health?.modelLoaded}>Open scorer</button></div> : null}
            <div className="table-bottom"><span>Showing {filteredQueue.length} of {queue.length} loaded rows</span><span>Reviewer decisions are training feedback, not ground truth until independently validated.</span></div>
          </section>

          {showModels ? <section className="panel registry-panel" id="models"><div className="panel-heading"><div><div className="section-kicker">MODEL LIFECYCLE</div><h2>Model registry</h2><p>Version history and held-out evaluation metrics.</p></div><button className="icon-button" onClick={() => { setShowModels(false); if (activeSection === 'models') navigateToSection('overview') }} aria-label="Close registry"><X size={18} /></button></div>{isAdmin ? <div className="registry-actions"><div><strong>Retraining</strong><span>Uses the chronological dataset split plus reviewed labels in the training partition.</span></div><button className="button button-primary" onClick={() => void retrain()} disabled={busy}><RefreshCw size={14} className={busy ? 'spin' : ''} /> Retrain model</button></div> : null}<div className="table-scroll"><table><thead><tr><th>VERSION</th><th>STATE</th><th>PR-AUC</th><th>PRECISION</th><th>RECALL</th><th>THRESHOLD</th><th>TEST SET</th></tr></thead><tbody>{allModels.map((model) => <tr key={model.version}><td><span className="mono-version">{model.version}</span><span className="registry-date">{shortDate(model.createdAt)}</span></td><td>{model.isActive ? <span className="status-pill pill-cleared"><i />Active</span> : <span className="status-pill pill-muted"><i />Archived</span>}</td><td>{formatPct(model.prAuc, 2)}</td><td>{formatPct(model.precision, 2)}</td><td>{formatPct(model.recall, 2)}</td><td>{formatPct(model.threshold, 2)}</td><td>{model.testCount.toLocaleString()} rows / {model.fraudTestCount} fraud</td></tr>)}</tbody></table></div>{isAdmin && health?.modelLoaded ? <div className="threshold-editor"><div><strong>Active decision threshold</strong><span>Lower thresholds generally increase review volume and may increase recall.</span></div><div className="threshold-control"><input type="range" min="0.01" max="0.99" step="0.01" value={thresholdDraft} onChange={(e) => setThresholdDraft(Number(e.target.value))} /><strong>{formatPct(thresholdDraft)}</strong><button className="button button-secondary" onClick={() => void changeThreshold()} disabled={busy || Math.abs(thresholdDraft - (health.threshold ?? 0.5)) < 0.0001}>Apply</button></div></div> : null}</section> : null}

          <section className="panel monitoring-panel" id="monitoring-detail">
            <div className="panel-heading"><div><div className="section-kicker">PHASE 4 / MODEL OBSERVABILITY</div><h2>Monitoring & drift signals</h2><p>Recent scoring volume, threshold crossings, reviewer throughput and feature-shift screening.</p></div><button className="button button-secondary" onClick={() => void loadDashboard()} disabled={busy}><RefreshCw size={14} /> Refresh signals</button></div>
            <div className="monitoring-stat-grid">
              <Metric label="Scored / 24h" value={(monitoring?.scoredLast24Hours ?? 0).toLocaleString()} detail="Stored scoring requests" />
              <Metric label="Flagged / 24h" value={(monitoring?.flaggedLast24Hours ?? 0).toLocaleString()} detail={`${formatPct(monitoring?.flagRate ?? 0)} of scored volume`} />
              <Metric label="Pending review" value={(monitoring?.pendingReview ?? 0).toLocaleString()} detail="All currently pending cases" />
              <Metric label="Reviewed / 24h" value={(monitoring?.reviewedLast24Hours ?? 0).toLocaleString()} detail="Audited analyst decisions" />
            </div>
            <div className="monitoring-lower-grid">
              <div className="monitoring-subpanel"><div className="subpanel-heading"><strong>3-hour scoring activity</strong><span>Last 24 hours</span></div>
                {monitoring?.timeline?.some(x => x.scored > 0) ? <div className="monitoring-timeline">{monitoring.timeline.map((bucket) => { const max = Math.max(1, ...monitoring.timeline.map(x => x.scored)); return <div className="monitoring-bin" key={bucket.window} title={`${bucket.window}: ${bucket.scored} scored, ${bucket.flagged} flagged`}><div className="monitoring-bars"><i className="bar-total" style={{ height: `${Math.max(3, bucket.scored / max * 100)}%` }} /><i className="bar-flagged" style={{ height: `${bucket.scored ? Math.max(3, bucket.flagged / max * 100) : 3}%` }} /></div><span>{bucket.window}</span><small>{bucket.scored}</small></div> })}</div> : <div className="monitoring-empty">No scoring events recorded in the last 24 hours.</div>}
                <p className="monitoring-note">{monitoring?.timelineNote ?? 'Timeline data is loading.'}</p>
              </div>
              <div className="monitoring-subpanel"><div className="subpanel-heading"><strong>Risk distribution</strong><span>Recent sample</span></div>
                <div className="distribution-row"><span><i className="distribution-dot low" /> Low risk</span><strong>{monitoring?.lowRiskCount ?? 0}</strong></div>
                <div className="distribution-row"><span><i className="distribution-dot medium" /> Medium risk</span><strong>{monitoring?.mediumRiskCount ?? 0}</strong></div>
                <div className="distribution-row"><span><i className="distribution-dot high" /> Above threshold</span><strong>{monitoring?.highRiskCount ?? 0}</strong></div>
                <div className={`drift-status ${monitoring?.driftAlert ? 'drift-alert' : ''}`}><span className="drift-pulse" /><div><strong>{monitoring?.driftAlert ? 'Feature drift screening alert' : health?.modelLoaded ? 'Drift screening' : 'Model not trained'}</strong><small>Mean shift {formatFixed(monitoring?.meanFeatureShift, 2)} z · max {formatFixed(monitoring?.maxFeatureShift, 2)} z</small></div></div>
                <p className="monitoring-note">{monitoring?.driftMethod ?? 'Waiting for drift data.'}</p>
              </div>
            </div>
          </section>

          {showEvaluation ? <section className="panel evaluation-panel" id="evaluation-lab">
            <div className="panel-heading"><div><div className="section-kicker">PHASE 2 / MODEL VALIDATION</div><h2>Held-out evaluation lab</h2><p>Scores the active model against the same chronological test partition used during training; it does not write test rows into the live queue.</p></div><div className="panel-heading-actions"><button className="button button-secondary" onClick={() => void loadEvaluation()} disabled={evaluationLoading}><RefreshCw size={14} className={evaluationLoading ? 'spin' : ''} /> Re-run evaluation</button><button className="icon-button" onClick={() => { setShowEvaluation(false); if (activeSection === 'evaluation') navigateToSection('overview') }} aria-label="Close evaluation lab"><X size={18} /></button></div></div>
            {evaluationError ? <div className="banner banner-error"><AlertTriangle size={15} /><span>{evaluationError}</span></div> : null}
            {evaluationLoading ? <div className="monitoring-empty">Evaluating held-out records without changing the active model…</div> : evaluation ? <><div className="evaluation-meta"><span>Version <strong>{evaluation.version}</strong></span><span>Evaluated {shortDate(evaluation.evaluatedAt)}</span><span>Validation: {evaluation.validationCount.toLocaleString()} rows / {evaluation.validationFraudCount} fraud</span><span>Test: {evaluation.testCount.toLocaleString()} rows / {evaluation.testFraudCount} fraud</span></div>
              <div className="evaluation-metric-grid">
                <Metric label="PR-AUC (test)" value={formatPct(evaluation.prAuc, 2)} detail="Ranking quality on test holdout" />
                <Metric label="Precision" value={formatPct(evaluation.testMetrics.precision, 2)} detail="At active threshold" />
                <Metric label="Recall" value={formatPct(evaluation.testMetrics.recall, 2)} detail="At active threshold" />
                <Metric label="F1-score" value={formatPct(evaluation.testMetrics.f1, 2)} detail="Precision / recall balance" />
                <Metric label="Accuracy" value={formatPct(evaluation.testMetrics.accuracy, 2)} detail="Not sufficient by itself for imbalanced data" />
                <Metric label="False-positive rate" value={formatPct(evaluation.testMetrics.falsePositiveRate, 2)} detail="Legitimate cases flagged" />
              </div>
              <div className="evaluation-lower-grid"><div className="monitoring-subpanel"><div className="subpanel-heading"><strong>Confusion matrix</strong><span>Active threshold {formatPct(evaluation.testMetrics.threshold, 2)}</span></div><table className="confusion-table"><thead><tr><th>Actual / Predicted</th><th>Legitimate</th><th>Fraud</th></tr></thead><tbody><tr><th>Legitimate</th><td>{evaluation.testMetrics.trueNegative.toLocaleString()} <small>TN</small></td><td className="cell-warning">{evaluation.testMetrics.falsePositive.toLocaleString()} <small>FP</small></td></tr><tr><th>Fraud</th><td className="cell-warning">{evaluation.testMetrics.falseNegative.toLocaleString()} <small>FN</small></td><td>{evaluation.testMetrics.truePositive.toLocaleString()} <small>TP</small></td></tr></tbody></table><div className="evaluation-footline">Flagged by model: {evaluation.testMetrics.predictedPositive.toLocaleString()} of {evaluation.testMetrics.total.toLocaleString()} test rows.</div></div>
                <div className="monitoring-subpanel"><div className="subpanel-heading"><strong>Threshold sensitivity</strong><span>Validation set only</span></div><div className="table-scroll"><table><thead><tr><th>THRESHOLD</th><th>PRECISION</th><th>RECALL</th><th>F1</th><th>FLAGGED</th></tr></thead><tbody>{evaluation.validationThresholdAnalysis.map(point => <tr key={point.threshold}><td>{formatPct(point.threshold, 0)}</td><td>{formatPct(point.precision, 1)}</td><td>{formatPct(point.recall, 1)}</td><td>{formatPct(point.f1, 1)}</td><td>{point.predictedPositive.toLocaleString()}</td></tr>)}</tbody></table></div></div></div>
              <div className="evaluation-caveat"><ShieldCheck size={16} /><div><strong>Evaluation boundary</strong><p>{evaluation.thresholdMethod} {evaluation.testSetCaveat}</p></div></div>
            </> : <div className="monitoring-empty">Select “Run held-out evaluation” to generate metrics for the active model.</div>}
          </section> : null}

          {showAudit ? <section className="panel audit-panel" id="audit-trail">
            <div className="panel-heading"><div><div className="section-kicker">PHASE 3 / ANALYST OPERATIONS</div><h2>Review audit trail</h2><p>Recent decisions with reviewer identity, rationale, outcome, risk, and model version.</p></div><div className="panel-heading-actions"><button className="button button-secondary" onClick={() => void loadAudit()} disabled={auditLoading}><RefreshCw size={14} className={auditLoading ? 'spin' : ''} /> Refresh audit</button><button className="icon-button" onClick={() => { setShowAudit(false); if (activeSection === 'audit-trail') navigateToSection('overview') }} aria-label="Close audit trail"><X size={18} /></button></div></div>
            {auditError ? <div className="banner banner-error"><AlertTriangle size={15} /><span>{auditError}</span></div> : null}
            {auditLoading ? <div className="monitoring-empty">Loading recorded decisions…</div> : <><div className="table-scroll"><table><thead><tr><th>WHEN</th><th>TRANSACTION</th><th>AMOUNT</th><th>OUTCOME</th><th>REVIEWER</th><th>NOTE</th><th>MODEL</th></tr></thead><tbody>{auditRows.map(row => <tr key={row.id}><td>{shortDate(row.createdAt)}</td><td><strong>{row.externalId}</strong><span className="registry-date">{row.transactionId.slice(0, 8).toUpperCase()}</span></td><td>{formatMoney(row.amount)}</td><td><span className={`status-pill ${row.isFraud ? 'pill-danger' : 'pill-cleared'}`}><i />{row.isFraud ? 'Fraud confirmed' : 'Legitimate'}</span></td><td>{row.reviewerEmail}</td><td className="audit-note">{row.note?.trim() || 'No note recorded'}</td><td><span className="mono-version">{row.modelVersion.slice(0, 13)}</span></td></tr>)}</tbody></table></div>{auditRows.length === 0 ? <div className="monitoring-empty">No analyst decisions recorded yet. Resolve a pending case in Review queue to create the first audit entry.</div> : <div className="table-bottom">Showing {auditRows.length} most recent reviewed cases. Reviewer decisions are not benchmark ground truth unless independently verified.</div>}</>}
          </section> : null}

          <footer className="page-footer"><span><Fingerprint size={14} /> SENTINEL FRAUD INTELLIGENCE</span><span><span className="footer-mini-dot" /> Services operational status is not model accuracy</span><span>Demo console · v1.0</span></footer>
        </div>
      </main>

      {reviewing ? <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setReviewing(null) }}><div className="review-modal" role="dialog" aria-modal="true" aria-labelledby="review-title"><div className="modal-heading"><div className="modal-icon"><ShieldAlert size={20} /></div><button className="icon-button" onClick={() => setReviewing(null)} aria-label="Close"><X size={18} /></button></div><div className="section-kicker">ANALYST REVIEW</div><h2 id="review-title">Resolve flagged transaction</h2><p className="modal-subtitle">{reviewing.externalId} · {formatMoney(reviewing.amount)} · {formatPct(reviewing.riskScore, 2)} risk probability</p><div className="verdict-options"><button className={selectedVerdict === true ? 'verdict-option verdict-fraud chosen' : 'verdict-option verdict-fraud'} onClick={() => setSelectedVerdict(true)}><span><ShieldAlert size={18} /></span><strong>Confirm fraud</strong><small>Positive training label</small>{selectedVerdict === true ? <CheckCircle2 size={16} className="verdict-check" /> : null}</button><button className={selectedVerdict === false ? 'verdict-option verdict-legitimate chosen' : 'verdict-option verdict-legitimate'} onClick={() => setSelectedVerdict(false)}><span><ShieldCheck size={18} /></span><strong>Mark legitimate</strong><small>Negative training label</small>{selectedVerdict === false ? <CheckCircle2 size={16} className="verdict-check" /> : null}</button></div><label className="note-label">Review note <span>Optional</span><textarea value={reviewNote} onChange={(e) => setReviewNote(e.target.value)} rows={3} maxLength={2000} placeholder="Add concise rationale for auditability…" /></label>{reviewing.explanations?.length ? <div className="review-explanations"><div className="review-explanation-heading"><strong>Model feature sensitivity</strong><span>Not causal attribution</span></div><p>{reviewing.explanationMethod}</p>{reviewing.explanations.map((ex) => <div className="explanation-row" key={ex.name}><span>{ex.name}</span><span className={ex.probabilityDelta === null || !Number.isFinite(ex.probabilityDelta) ? '' : ex.probabilityDelta >= 0 ? 'text-warning' : 'text-good'}>{formatDeltaPp(ex.probabilityDelta)}</span></div>)}</div> : null}<div className="modal-footnote"><LockKeyhole size={14} /> The decision is audited and included in a future retraining run.</div><div className="modal-actions"><button className="button button-secondary" onClick={() => setReviewing(null)}>Cancel</button><button className="button button-primary" onClick={() => void submitReview()} disabled={busy || selectedVerdict === null}>{busy ? 'Saving…' : 'Save decision'} <Check size={15} /></button></div></div></div> : null}
    </div>
  )
}

function StatCard({ label, value, note, icon, tone, trend }: { label: string; value: string; note: string; icon: ReactNode; tone: string; trend: string }) {
  return <article className="stat-card"><div className="stat-top"><span>{label}</span><div className={`stat-icon ${tone}`}>{icon}</div></div><div className="stat-value">{value}</div><div className="stat-bottom"><span>{note}</span><span className={`stat-trend ${tone}`}>{tone === 'orange' ? <ArrowUpRight size={12} /> : tone === 'green' ? <ArrowDownRight size={12} /> : <Activity size={12} />}{trend}</span></div></article>
}
function Metric({ label, value, detail }: { label: string; value: string; detail: string }) {
  return <div className="model-metric"><span>{label}</span><strong>{value}</strong><small>{detail}</small></div>
}
function EmptyChart({ modelLoaded }: { modelLoaded: boolean }) {
  return <div className="empty-chart"><div className="empty-chart-symbol"><Activity size={23} /></div><strong>{modelLoaded ? 'No scored activity yet' : 'Model is waiting for training'}</strong><span>{modelLoaded ? 'Risk probabilities will appear after the first scoring request.' : 'The dashboard stays available while the dataset and model are being prepared.'}</span><div className="empty-chart-lines"><i /><i /><i /><i /></div></div>
}
function LoginScreen({ email, setEmail, password, setPassword, registerMode, setRegisterMode, onSubmit, busy, error }: {
  email: string; setEmail: (value: string) => void; password: string; setPassword: (value: string) => void;
  registerMode: boolean; setRegisterMode: (value: boolean) => void;
  onSubmit: (event: FormEvent<HTMLFormElement>) => void; busy: boolean; error: string
}) {
  return <main className="login-shell">
    <div className="login-decoration grid-glow" aria-hidden="true" />
    <header className="login-topline">
      <div className="brand-lockup"><div className="brand-mark"><ShieldCheck size={22} /></div><div><span className="brand-name">SENTINEL</span><span className="brand-subtitle">FRAUD INTELLIGENCE</span></div></div>
      <div className="login-top-signal"><span className="signal-pulse" /> RISK OBSERVATORY <span className="signal-divider">/</span> LOCAL ENVIRONMENT</div>
      <div className="login-top-meta"><Fingerprint size={15} /> MODEL-DRIVEN · HUMAN-REVIEWED</div>
    </header>

    <section className="login-stage">
      <div className="stage-index"><span>01</span> / SIGNAL INTAKE</div>
      <h1>Every transaction<br />leaves a <span>signal.</span></h1>
      <p>Follow the pattern, inspect the outlier, and put analysts in control. Sentinel turns transaction features into a reviewable risk signal, not an unexplained verdict.</p>
      <div className="stage-orbit-labels"><span><i className="orbit-point orbit-cyan" /> Live scoring</span><span><i className="orbit-point orbit-amber" /> Risk review</span><span><i className="orbit-point orbit-violet" /> Model watch</span></div>
    </section>

    <section className="login-authorization" aria-labelledby="auth-title">
      <div className="auth-rail-intro">
        <div className="auth-rail-icon"><LockKeyhole size={19} /></div>
        <div className="auth-rail-copy"><div className="section-kicker">AUTHORIZATION GATE</div><h2 id="auth-title">{registerMode ? 'Request analyst access' : 'Enter the signal room'}</h2><p>{registerMode ? 'Create a standard analyst account for the local workspace.' : 'Your credentials open the live risk workspace.'}</p></div>
      </div>
      <form className="login-form" onSubmit={onSubmit}>
        <label>Email address<div className="input-icon-wrap"><UserRound size={16} /><input type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="analyst@company.com" autoComplete="email" required /></div></label>
        <label>Password<div className="input-icon-wrap"><LockKeyhole size={16} /><input type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder={registerMode ? '12+ characters' : 'Your local admin password'} autoComplete={registerMode ? 'new-password' : 'current-password'} minLength={registerMode ? 12 : 1} required /></div></label>
        <button className="button button-primary login-submit" type="submit" disabled={busy}>{busy ? <RefreshCw className="spin" size={16} /> : <ArrowUpRight size={17} />}{busy ? 'Opening workspace…' : registerMode ? 'Create access' : 'Initialize session'}</button>
      </form>
      {error ? <div className="auth-error"><AlertTriangle size={16} />{error}</div> : null}
      {registerMode ? <div className="password-hint">Use 12+ characters with upper/lowercase, a number, and a symbol.</div> : null}
      <div className="auth-rail-foot"><span><ShieldCheck size={14} /> JWT-secured · Admin actions are role-restricted</span><button onClick={() => setRegisterMode(!registerMode)}>{registerMode ? 'Back to sign in' : 'Create analyst access'} <ArrowUpRight size={13} /></button></div>
    </section>
    <footer className="login-bottomline"><span>01 / SENTINEL RISK OBSERVATORY</span><span>PR-AUC · PRECISION · RECALL · SHIFT SIGNALS</span><span>LOCAL DEMO · NOT FOR LIVE PAYMENT DECISIONS</span></footer>
  </main>
}
