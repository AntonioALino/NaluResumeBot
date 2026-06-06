# 🏗️ NaluResumeBot — Architecture

> Documento de arquitetura técnica do sistema. Descreve componentes, fluxos, contratos de dados e decisões de design.

---

## Índice

1. [Visão Geral](#1-visão-geral)
2. [Diagrama de Componentes](#2-diagrama-de-componentes)
3. [Fluxo de Processamento](#3-fluxo-de-processamento)
4. [Componentes em Detalhe](#4-componentes-em-detalhe)
5. [Contratos de Mensageria](#5-contratos-de-mensageria)
6. [Modelo de Dados](#6-modelo-de-dados)
7. [Configuração de Infraestrutura](#7-configuração-de-infraestrutura)
8. [Segurança e Limites](#8-segurança-e-limites)
9. [Estratégia de Evolução (POC → Produção)](#9-estratégia-de-evolução-poc--produção)

---

## 1. Visão Geral

O NaluResumeBot é um sistema de processamento assíncrono de PDFs acionado via Discord. A arquitetura é orientada a eventos: cada etapa do pipeline publica um evento numa fila, e o componente responsável o consome independentemente.

### Princípios de Design

| Princípio | Como Aplicado |
|---|---|
| **Assincronicidade** | Nenhum worker bloqueia outro; tudo via filas RabbitMQ |
| **Responsabilidade Única** | Cada worker faz UMA coisa bem feita |
| **Baixo acoplamento** | Workers se comunicam por contrato (mensagem), não por chamada direta |
| **Escalabilidade horizontal** | Qualquer worker pode ter N réplicas sem mudança de código |
| **POC-first** | Complexidade adicionada só quando validada por uso real |

---

## 2. Diagrama de Componentes

```
┌─────────────────────────────────────────────────────────────────┐
│                        DISCORD (externo)                        │
│                    canal: #estudos-nalu                         │
└───────────────────────────┬─────────────────────────────────────┘
                            │ upload PDF / comando
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    DISCORD WORKER (Python)                      │
│  - Escuta eventos do Discord via discord.py                     │
│  - Faz download do PDF em memória                               │
│  - Valida tamanho e tipo do arquivo                             │
│  - Publica job na fila RabbitMQ                                 │
│  - Escuta fila de respostas e envia embeds                      │
└───────────┬──────────────────────────────────────┬──────────────┘
            │ publica: pdf.extract                 ▲ consome: discord.respond
            ▼                                      │
┌───────────────────────────────────────────────────────────────────────────┐
│                            RABBITMQ                                       │
│                                                                           │
│   pdf.extract ──► pdf_worker                                              │
│   ai.process  ──► ai_worker                                               │
│   discord.respond ──► discord_worker                                      │
└───────────────────────────────────────────────────────────────────────────┘
            │ publica: ai.process                  ▲ publica: discord.respond
            ▼                                      │
┌───────────────────────┐          ┌───────────────────────────────────────┐
│   PDF WORKER (Python) │          │           AI WORKER (Python)          │
│                       │          │                                        │
│  - pdfplumber         │ ────────►│  - Gemini SDK                         │
│  - Extrai texto       │          │  - Gera resumo estruturado             │
│  - Chunking           │          │  - Gera flashcards                    │
│  - Detecta seções     │          │  - Gera mapa mental (markdown)        │
└───────────────────────┘          │  - (pós-POC) quiz, RAG, TTS           │
                                   └───────────────────────────────────────┘
                                                    │
                            ┌───────────────────────┼───────────────────────┐
                            ▼                       ▼                       ▼
                   ┌─────────────────┐   ┌──────────────────┐   ┌──────────────────┐
                   │   ORQUESTRADOR  │   │    POSTGRESQL     │   │      REDIS       │
                   │   (.NET 10)     │   │                   │   │                  │
                   │                 │   │  - documents      │   │  - job status    │
                   │  - API REST     │   │  - summaries      │   │  - chat sessions │
                   │  - Job tracking │   │  - flashcards     │   │  - rate limits   │
                   │  - Persistência │   │  - quiz_questions │   │                  │
                   └─────────────────┘   └──────────────────┘   └──────────────────┘
```

---

## 3. Fluxo de Processamento

### 3.1 — Happy Path (PDF → Resposta)

```
Usuária                Discord Worker       RabbitMQ        PDF Worker        AI Worker
   │                        │                  │                │                │
   │── envia PDF ──────────►│                  │                │                │
   │                        │── valida arquivo │                │                │
   │                        │── cria document  │                │                │
   │◄── "⏳ Processando..." ─│                  │                │                │
   │                        │── publish ───────►│                │                │
   │                        │   pdf.extract    │◄── consume ────│                │
   │                        │                  │                │── extrai texto  │
   │                        │                  │                │── faz chunking  │
   │                        │                  │── publish ─────►│                │
   │                        │                  │   ai.process   │◄── consume ────│
   │                        │                  │                │                │── chama Gemini
   │                        │                  │                │                │── monta resultado
   │                        │                  │◄── publish ────│                │
   │                        │                  │  discord.respond                │
   │                        │◄── consume ──────│                │                │
   │◄── embeds formatados ──│                  │                │                │
   │    (resumo + cards)    │                  │                │                │
```

### 3.2 — Feedback em Tempo Real via Redis

O Discord Worker atualiza o status do job no Redis a cada etapa. Isso permite que o bot edite a mensagem de "aguarde" com o progresso atual:

```
⏳ PDF recebido, extraindo texto...
⚙️  Texto extraído (12.430 palavras), gerando resumo...
🧠 Gerando flashcards...
✅ Pronto! Veja abaixo ↓
```

Chave Redis: `job:{job_id}:status` — TTL de 1 hora.

---

## 4. Componentes em Detalhe

### 4.1 Discord Worker

**Linguagem:** Python 3.12  
**Biblioteca:** `discord.py` (v2.x)  
**Responsabilidades:**

- Monitorar o canal configurado por uploads de arquivos `.pdf`
- Validar tamanho (limite configurável, default 20MB) e tipo MIME
- Fazer download do arquivo em memória (sem salvar em disco na POC)
- Publicar job em `pdf.extract` com o binário do PDF em base64
- Consumir fila `discord.respond` e montar embeds formatados
- Editar a mensagem de status com feedback em tempo real via Redis

**Comandos Discord (pós-POC):**

| Comando | Ação |
|---|---|
| `/resumo` | Reprocessa o último PDF enviado |
| `/flashcards` | Lista flashcards do último documento |
| `/quiz` | Inicia sessão de quiz interativo |
| `/chat` | Abre sessão de perguntas sobre o PDF |

---

### 4.2 PDF Worker

**Linguagem:** Python 3.12  
**Biblioteca:** `pdfplumber`  
**Responsabilidades:**

- Consumir fila `pdf.extract`
- Extrair texto preservando estrutura (títulos, listas, parágrafos)
- Detectar e separar seções/capítulos pelo padrão tipográfico
- Aplicar chunking inteligente respeitando limites de contexto do Gemini
- Publicar resultado em `ai.process`

**Estratégia de Chunking:**

```
PDF
 │
 ├── Detecção de seções (por tamanho de fonte, negrito, padrão numérico)
 │
 ├── Chunk por seção (preferencial) — mantém contexto semântico
 │
 └── Chunk por tamanho fixo (fallback) — máx. 8.000 tokens por chunk
```

Para PDFs pequenos (< 50 páginas na POC), processa em um único chunk para preservar coerência do resumo.

---

### 4.3 AI Worker

**Linguagem:** Python 3.12  
**Biblioteca:** `google-generativeai`  
**Modelo:** `gemini-1.5-flash` (POC) → `gemini-1.5-pro` (produção)  
**Responsabilidades:**

- Consumir fila `ai.process`
- Executar prompts especializados para cada tipo de output
- Publicar resultado em `discord.respond`
- Persistir outputs no Postgres via chamada ao Orquestrador

**Prompts por Feature:**

```
ai_worker/prompts/
  ├── summary.py      → Resumo em tópicos hierárquicos, linguagem clara
  ├── flashcards.py   → Pares pergunta/resposta objetivos e testáveis
  ├── mindmap.py      → Markdown compatível com markmap.js
  └── quiz.py         → (pós-POC) Múltipla escolha com justificativa
```

**Estratégia de Prompt (System Prompt base):**

```
Você é um assistente de estudos especializado. Analise o conteúdo acadêmico
fornecido e gere materiais de estudo precisos, objetivos e pedagogicamente
eficientes. Responda sempre em português brasileiro. Priorize o que é mais
provável de cair em prova.
```

---

### 4.4 Orquestrador (.NET 10)

**Framework:** ASP.NET Core 10  
**Responsabilidades na POC:**

- Persistir documentos e outputs no Postgres
- Expor endpoints REST para consulta de histórico
- Gerenciar estado de jobs (criação, atualização, conclusão)
- Centralizar configurações de infra (connection strings, feature flags)

**Endpoints REST (POC):**

```
POST   /api/jobs                  → Cria novo job de processamento
GET    /api/jobs/{id}             → Status de um job
GET    /api/documents             → Lista documentos processados
GET    /api/documents/{id}/summary    → Resumo de um documento
GET    /api/documents/{id}/flashcards → Flashcards de um documento
```

---

## 5. Contratos de Mensageria

Todas as mensagens trafegam em JSON via RabbitMQ.

### Fila: `pdf.extract`

Publicado pelo **Discord Worker**, consumido pelo **PDF Worker**.

```json
{
  "job_id": "uuid-v4",
  "document_id": "uuid-v4",
  "discord_channel_id": "123456789",
  "discord_message_id": "987654321",
  "filename": "anatomia-cap3.pdf",
  "pdf_base64": "JVBERi0xLjQK...",
  "requested_at": "2025-06-06T10:30:00Z"
}
```

### Fila: `ai.process`

Publicado pelo **PDF Worker**, consumido pelo **AI Worker**.

```json
{
  "job_id": "uuid-v4",
  "document_id": "uuid-v4",
  "discord_channel_id": "123456789",
  "discord_message_id": "987654321",
  "filename": "anatomia-cap3.pdf",
  "chunks": [
    {
      "index": 0,
      "section": "Introdução",
      "text": "...",
      "token_count": 3200
    }
  ],
  "total_pages": 24,
  "extracted_at": "2025-06-06T10:30:05Z"
}
```

### Fila: `discord.respond`

Publicado pelo **AI Worker**, consumido pelo **Discord Worker**.

```json
{
  "job_id": "uuid-v4",
  "discord_channel_id": "123456789",
  "discord_message_id": "987654321",
  "document_id": "uuid-v4",
  "filename": "anatomia-cap3.pdf",
  "outputs": {
    "summary": {
      "title": "Anatomia — Capítulo 3",
      "sections": [
        {
          "heading": "Sistema Nervoso Central",
          "bullets": ["...", "..."]
        }
      ]
    },
    "flashcards": [
      { "front": "O que é a bainha de mielina?", "back": "Camada lipídica que envolve o axônio e acelera a condução do impulso nervoso." }
    ],
    "mindmap_markdown": "# Anatomia Cap.3\n## Sistema Nervoso\n### Central\n..."
  },
  "processed_at": "2025-06-06T10:30:18Z"
}
```

---

## 6. Modelo de Dados

### PostgreSQL

```sql
-- Rastreia cada PDF enviado
CREATE TABLE documents (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    filename            TEXT NOT NULL,
    discord_channel_id  TEXT NOT NULL,
    discord_message_id  TEXT,
    total_pages         INT,
    total_tokens        INT,
    status              TEXT NOT NULL DEFAULT 'pending',
    -- 'pending' | 'extracting' | 'processing' | 'done' | 'error'
    error_message       TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Resumo estruturado por seções
CREATE TABLE summaries (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    title       TEXT,
    content     JSONB NOT NULL,   -- array de { heading, bullets[] }
    model_used  TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Flashcards gerados por documento
CREATE TABLE flashcards (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    front       TEXT NOT NULL,
    back        TEXT NOT NULL,
    difficulty  SMALLINT DEFAULT 0,  -- pós-POC: 0=nova, 1=fácil, 2=média, 3=difícil
    next_review TIMESTAMPTZ,         -- pós-POC: spaced repetition
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Mapa mental em markdown (markmap-compatible)
CREATE TABLE mindmaps (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    content     TEXT NOT NULL,  -- markdown hierárquico
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- pós-POC: quiz
CREATE TABLE quiz_questions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id     UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    question        TEXT NOT NULL,
    options         JSONB NOT NULL,     -- ["opção A", "opção B", "opção C", "opção D"]
    correct_index   SMALLINT NOT NULL,  -- 0-3
    explanation     TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Índices
CREATE INDEX idx_documents_status ON documents(status);
CREATE INDEX idx_flashcards_document ON flashcards(document_id);
CREATE INDEX idx_flashcards_next_review ON flashcards(next_review); -- pós-POC
```

### Redis — Chaves

| Chave | Tipo | Valor | TTL |
|---|---|---|---|
| `job:{job_id}:status` | String | `extracting` / `processing` / `done` | 1h |
| `job:{job_id}:discord_msg` | String | ID da mensagem de status no Discord | 1h |
| `chat:{channel_id}:history` | List | JSON da conversa contextual | 2h |
| `ratelimit:{channel_id}` | String | contador de PDFs por hora | 1h |

---

## 7. Configuração de Infraestrutura

### docker-compose.yml (local)

```yaml
version: '3.9'

services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: nalubot
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: postgres
    ports:
      - "5432:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"

  rabbitmq:
    image: rabbitmq:3.13-management-alpine
    ports:
      - "5672:5672"
      - "15672:15672"   # UI de gerenciamento: http://localhost:15672
    environment:
      RABBITMQ_DEFAULT_USER: guest
      RABBITMQ_DEFAULT_PASS: guest

volumes:
  pgdata:
```

### Filas RabbitMQ — Configuração

```
Exchange: nalubot.direct (tipo: direct)
  │
  ├── Routing Key: pdf.extract  → Queue: pdf.extract   (durable: true)
  ├── Routing Key: ai.process   → Queue: ai.process    (durable: true)
  └── Routing Key: discord.respond → Queue: discord.respond (durable: true)

Dead Letter Exchange: nalubot.dlx
  └── Queue: nalubot.dead  (mensagens com 3 tentativas falhas)
```

---

## 8. Segurança e Limites

### Limites Operacionais (POC)

| Limite | Valor | Onde Aplicado |
|---|---|---|
| Tamanho máximo do PDF | 20MB | Discord Worker (validação) |
| PDFs por hora por canal | 10 | Redis (rate limiting) |
| Timeout de extração | 30s | PDF Worker |
| Timeout de IA | 120s | AI Worker |
| Máximo de flashcards | 30 por documento | AI Worker (prompt) |
| Máximo de chunks por PDF | 10 | PDF Worker |

### Validações

- Tipo MIME verificado antes do download (`application/pdf`)
- PDF Worker rejeita arquivos corrompidos ou protegidos por senha
- AI Worker valida JSON de resposta do Gemini antes de publicar
- Orquestrador aplica idempotência por `job_id` (reprocessamentos não criam duplicatas)

---

## 9. Estratégia de Evolução (POC → Produção)

A arquitetura foi desenhada para escalar progressivamente sem rewrites.

### O que NÃO muda

- Contratos de mensageria (filas e schemas JSON)
- Modelo de dados Postgres (apenas novas tabelas/colunas)
- Interface do Orquestrador REST

### Evoluções Planejadas

```
POC  ─────────────────────────────────────────────────────────► Produção
  │                                                                  │
  │  Workers rodam local                    Workers em containers    │
  │  1 instância de cada          ──►       N réplicas por worker   │
  │                                                                   │
  │  Gemini 1.5 Flash (custo baixo)  ──►   Gemini 1.5 Pro + cache  │
  │                                                                   │
  │  Sem auth                         ──►   Discord OAuth           │
  │                                                                   │
  │  Chat: sem RAG                    ──►   RAG com pgvector        │
  │                                                                   │
  │  Flashcards simples               ──►   SM-2 spaced repetition  │
  │                                                                   │
  │  Background: RabbitMQ workers     ──►   .NET Worker Services    │
  └──────────────────────────────────────────────────────────────────┘
```

### Adição de RAG (chat contextual)

Quando implementado, o PDF Worker também gerará embeddings e os armazenará no `pgvector` (extensão do Postgres). Nenhum outro componente muda o AI Worker simplesmente passa a fazer busca semântica antes de chamar o Gemini.

```sql
-- pós-POC: extensão para embeddings
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE document_chunks (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id UUID NOT NULL REFERENCES documents(id),
    chunk_index INT NOT NULL,
    text        TEXT NOT NULL,
    embedding   vector(768),   -- dimensão do modelo de embedding
    created_at  TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX ON document_chunks USING ivfflat (embedding vector_cosine_ops);
```

---

*Última atualização: Junho 2025*