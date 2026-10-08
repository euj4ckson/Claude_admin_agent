import fs from 'node:fs';
import path from 'node:path';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import { AppError, attachmentName, now, ticketId } from './core.mjs';

const exec = promisify(execFile);
const AZURE_HOST = 'dev.azure.com';
const MAX_ATTACHMENT_BYTES = 50 * 1024 * 1024;
const MAX_TOTAL_ATTACHMENT_BYTES = 200 * 1024 * 1024;

function azureSettings(config = {}) {
  const value = config.azure || {};
  return {
    organization: String(value.organization || 'sistemasunion'),
    project: String(value.project || 'SSUnion'),
    host: String(value.host || AZURE_HOST)
  };
}

function decode(value) { try { return decodeURIComponent(value); } catch { return value; } }

export function parseAzureReference(value, config = {}) {
  const settings = azureSettings(config);
  const raw = String(value ?? '').trim();
  if (!raw) throw new AppError('Cole o link do work item ou informe o ID numérico do Azure.');
  if (/^\d{1,10}$/.test(raw)) {
    const id = Number(raw);
    return { id, url: `https://${settings.host}/${settings.organization}/${encodeURIComponent(settings.project)}/_workitems/edit/${id}` };
  }
  let url;
  try { url = new URL(raw); } catch { throw new AppError('O link do Azure não é válido.'); }
  if (url.protocol !== 'https:' || url.hostname.toLowerCase() !== settings.host.toLowerCase()) throw new AppError(`Use um link HTTPS de ${settings.host}.`);
  const parts = url.pathname.split('/').filter(Boolean).map(decode);
  if (parts.length < 2 || parts[0].toLowerCase() !== settings.organization.toLowerCase() || parts[1].toLowerCase() !== settings.project.toLowerCase()) throw new AppError(`O link deve pertencer ao projeto ${settings.organization}/${settings.project}.`);
  const queryId = url.searchParams.get('workitem') || url.searchParams.get('workItem');
  const pathMatch = url.pathname.match(/(?:_workitems\/edit|workitems\/edit)\/(\d+)/i);
  const id = Number(queryId || pathMatch?.[1]);
  if (!Number.isSafeInteger(id) || id <= 0 || id > 9_999_999_999) throw new AppError('Não encontrei um ID de work item nesse link.');
  return { id, url: `https://${settings.host}/${settings.organization}/${encodeURIComponent(settings.project)}/_workitems/edit/${id}` };
}

function authHeader(pat) { return `Basic ${Buffer.from(`:${pat}`).toString('base64')}`; }

async function request(url, pat, fetchImpl = globalThis.fetch, options = {}) {
  if (!pat) throw new AppError('Configure um PAT do Azure com permissão somente de leitura antes de importar.');
  let response;
  try { response = await fetchImpl(url, { ...options, headers: { Accept: 'application/json', Authorization: authHeader(pat), ...(options.headers || {}) }, signal: options.signal || AbortSignal.timeout(30_000) }); }
  catch (e) { throw new AppError(`Não foi possível consultar o Azure: ${e.message}`); }
  if (!response.ok) {
    const message = response.status === 401 || response.status === 403 ? 'O PAT do Azure foi recusado ou não tem permissão de leitura.' : `O Azure retornou HTTP ${response.status}.`;
    throw new AppError(message, response.status === 404 ? 404 : 409);
  }
  return response;
}

function decodeEntities(value) {
  return String(value).replace(/&nbsp;/gi, ' ').replace(/&amp;/gi, '&').replace(/&lt;/gi, '<').replace(/&gt;/gi, '>').replace(/&quot;/gi, '"').replace(/&#39;|&apos;/gi, "'").replace(/&#(\d+);/g, (_, n) => String.fromCodePoint(Number(n)));
}

export function htmlToText(value) {
  return decodeEntities(String(value || '').replace(/<script[\s\S]*?<\/script>|<style[\s\S]*?<\/style>/gi, '').replace(/<br\s*\/?>/gi, '\n').replace(/<\/p\s*>|<\/div\s*>|<\/li\s*>|<\/h[1-6]\s*>/gi, '\n').replace(/<li[^>]*>/gi, '- ').replace(/<[^>]+>/g, '').replace(/\r/g, '')).split('\n').map(x => x.replace(/[ \t]+/g, ' ').trim()).filter((x, i, a) => x || a[i - 1]).join('\n').trim();
}

function sprintFrom(fields) {
  const candidates = Object.entries(fields || []).filter(([key]) => /sprint|iteration/i.test(key)).map(([, value]) => String(value ?? ''));
  for (const value of candidates) { const found = value.match(/\b(?:sprint|iteração|iteration)\s*[\/_-]?\s*(\d{1,6})\b/i); if (found) return found[1]; }
  return '';
}

function ticketFrom(text) { const found = String(text || '').match(/\b([A-Z]{2,8}\d{2,8})\b/i); return found ? ticketId(found[1]) : ''; }

export function safeRemoteName(value, fallback = 'anexo-azure.bin') {
  let name = String(value || '').split(/[\\/]/).pop().replace(/[<>:"|?*\x00-\x1f]/g, '_').replace(/[. ]+$/g, '').trim();
  if (!name || name === '.' || name === '..') name = fallback;
  return attachmentName(name.slice(0, 180));
}

function repositorySuggestion(ticket, config) {
  const mapping = { PDV: 'ss_pdv', FVW: 'ss_Forca_de_Vendas_Web', FVA: 'ss_fva', WCF: 'ss_wcf_fvan', FAT: 'ss_erp_tag1', GER: 'ss_erp_tag1', ADM: 'ss_erp_tag1' };
  const prefix = String(ticket || '').match(/^[A-Z]+/i)?.[0]?.toUpperCase();
  const id = mapping[prefix];
  const repositoriesRoot = config.repositories_root || '';
  if (!id) return null;
  try { const entries = fs.readdirSync(repositoriesRoot, { withFileTypes: true }); const match = entries.find(x => x.isDirectory() && x.name.toLowerCase() === id.toLowerCase()); return match?.name || null; } catch { return id; }
}

export async function fetchWorkItem(reference, config, options = {}) {
  const parsed = parseAzureReference(reference, config), settings = azureSettings(config), pat = options.pat;
  const endpoint = `https://${settings.host}/${settings.organization}/${encodeURIComponent(settings.project)}/_apis/wit/workitems/${parsed.id}?$expand=relations&api-version=7.1`;
  const response = await request(endpoint, pat, options.fetchImpl);
  const item = await response.json();
  const fields = item.fields || {}, title = String(fields['System.Title'] || '').trim();
  const description = htmlToText(fields['System.Description'] || fields['Microsoft.VSTS.Common.Description'] || '');
  const ticket = ticketFrom(`${title} ${fields['System.Tags'] || ''} ${description}`);
  const attachments = (item.relations || []).filter(x => String(x.rel || '').toLowerCase() === 'attachedfile' || /_apis\/wit\/attachments\//i.test(String(x.url || ''))).map((relation, index) => ({ name: safeRemoteName(relation.attributes?.name || relation.attributes?.title || '', `anexo-azure-${index + 1}.bin`), url: String(relation.url), size: Number(relation.attributes?.resourceSize) || null, comment: String(relation.attributes?.comment || '') })).filter(x => x.url);
  const sprint = fields['Custom.Sprint'] || fields['Custom.SprintNumber'] || sprintFrom(fields) || '';
  const scope = description || title;
  const warnings = [];
  if (!ticket) warnings.push('Não encontrei o código do ticket no título, tags ou descrição; informe-o manualmente.');
  if (!/^\d{1,6}$/.test(String(sprint))) warnings.push('Não encontrei uma sprint numérica; informe-a manualmente.');
  if (scope.length < 20) warnings.push('A descrição importada é curta; revise o escopo antes de cadastrar.');
  return { source: { id: parsed.id, url: parsed.url, organization: settings.organization, project: settings.project, importedAt: now() }, azure: { id: parsed.id, url: parsed.url }, fields: { title, description, state: String(fields['System.State'] || ''), tags: String(fields['System.Tags'] || ''), iteration: String(fields['System.IterationPath'] || ''), area: String(fields['System.AreaPath'] || '') }, suggested: { ticket, title: title.slice(0, 160), scope, sprint: String(sprint), repositoryId: repositorySuggestion(ticket, config), release: '', branchType: /^hotfix|bug/i.test(String(fields['System.Tags'] || '')) ? 'hotfix' : 'feature' }, attachments, warnings };
}

export async function downloadAzureAttachment(attachment, pat, options = {}) {
  const response = await request(attachment.url, pat, options.fetchImpl, { headers: { Accept: '*/*' } });
  const length = Number(response.headers.get('content-length') || 0);
  if (length > MAX_ATTACHMENT_BYTES) throw new AppError(`O anexo ${attachment.name} ultrapassa 50 MB.`);
  const buffer = Buffer.from(await response.arrayBuffer());
  if (buffer.length > MAX_ATTACHMENT_BYTES) throw new AppError(`O anexo ${attachment.name} ultrapassa 50 MB.`);
  return buffer;
}

export function attachmentLimits() { return { maxBytes: MAX_ATTACHMENT_BYTES, maxTotalBytes: MAX_TOTAL_ATTACHMENT_BYTES }; }

function powershellWithInput(script, input) {
  return new Promise((resolve, reject) => {
    const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '', stderr = ''; child.stdout.on('data', chunk => { stdout += chunk; }); child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject); child.on('close', code => code === 0 ? resolve(stdout.trim()) : reject(new Error(stderr || `PowerShell terminou com código ${code}`)));
    child.stdin.end(input);
  });
}

async function protect(value) {
  if (process.platform !== 'win32') return null;
  const script = "$b=[Convert]::FromBase64String([Console]::In.ReadToEnd()); Add-Type -AssemblyName System.Security; [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect($b,$null,[System.Security.Cryptography.DataProtectionScope]::CurrentUser))";
  try { return await powershellWithInput(script, Buffer.from(value, 'utf8').toString('base64')); } catch { return null; }
}

async function unprotect(value) {
  if (process.platform !== 'win32') return null;
  const script = "$b=[Convert]::FromBase64String([Console]::In.ReadToEnd()); Add-Type -AssemblyName System.Security; [Text.Encoding]::UTF8.GetString([System.Security.Cryptography.ProtectedData]::Unprotect($b,$null,[System.Security.Cryptography.DataProtectionScope]::CurrentUser))";
  try { return await powershellWithInput(script, value); } catch { return null; }
}

export async function saveAzurePat(dataRoot, pat) {
  const clean = String(pat || '').trim();
  if (clean.length < 20 || clean.length > 500 || /[\r\n]/.test(clean)) throw new AppError('Informe um PAT válido, sem quebras de linha.');
  const encrypted = await protect(clean);
  if (!encrypted) throw new AppError('Não foi possível proteger o PAT com o Windows. Use AZURE_DEVOPS_PAT no ambiente ou tente novamente.');
  const file = path.join(dataRoot, 'azure-credentials.json');
  fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, JSON.stringify({ version: 1, provider: 'windows-dpapi', pat: encrypted }) + '\n', { mode: 0o600 });
  return { configured: true };
}

export async function readAzurePat(dataRoot) {
  const fromEnv = String(process.env.AZURE_DEVOPS_PAT || '').trim();
  if (fromEnv) return fromEnv;
  try { const saved = JSON.parse(fs.readFileSync(path.join(dataRoot, 'azure-credentials.json'), 'utf8')); return await unprotect(saved.pat); } catch { return null; }
}

export function azureStatus(config, dataRoot) { return { configured: !!process.env.AZURE_DEVOPS_PAT || fs.existsSync(path.join(dataRoot, 'azure-credentials.json')), organization: azureSettings(config).organization, project: azureSettings(config).project, readOnly: true }; }
