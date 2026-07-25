# Pi Signage

Language for the local signage system that connects one operator-owned Windows
laptop to the Raspberry Pis driving the shop's TVs.

## Language

**Controller laptop**:
The client's single store laptop trusted to configure and control the Pi fleet.
_Avoid_: Control PC, admin computer

**Display Pi**:
A Raspberry Pi that stores signage state and drives one TV.
_Avoid_: Agent, screen

**Builder setup**:
The preparation and full Wi-Fi testing performed before delivery while the Display Pi temporarily trusts the builder's laptop.
_Avoid_: Pairing, ownership

**Store onboarding**:
The first USB connection at the client site that joins the Display Pi to store Wi-Fi and establishes its trusted Controller laptop.
_Avoid_: Builder setup, Wi-Fi setup

**Prepare for delivery**:
The builder action that erases test content, temporary identity, temporary controller trust, and builder Wi-Fi before shipment while preserving verified software, USB provisioning, and the Recovery PIN.
_Avoid_: Factory reset, Ownership recovery

**Ownership recovery**:
The USB-only process that replaces a lost or retired Controller laptop and invalidates its control credentials.
_Avoid_: Password reset, Wi-Fi reset

**Recovery PIN**:
A unique 8-digit numeric credential printed on the bottom of each Display Pi and required with USB access to establish or transfer ownership.
_Avoid_: Login password, controller secret

**Remote support**:
An attended Quick Assist session initiated and approved on the Controller laptop for builder-assisted diagnosis or repair.
_Avoid_: Ownership recovery, daily control

**Control request**:
A state-changing command signed with the unique secret shared by one Display Pi and its Controller laptop.
_Avoid_: Display traffic, login

## Relationships

- One **Controller laptop** controls one or more **Display Pis**
- Each **Display Pi** trusts exactly one **Controller laptop** for the time being
- Store staff normally share one Windows login on the **Controller laptop**
- Daily control is passwordless after **Store onboarding**
- **Builder setup** happens before delivery and does not establish permanent ownership
- Temporary trust created for **Builder setup** must be removed before delivery
- **Prepare for delivery** ends **Builder setup** and returns the **Display Pi** to an unowned state
- **Store onboarding** establishes the **Controller laptop** trusted by the **Display Pi**
- The builder's laptop never retains production control credentials
- **Ownership recovery** transfers each **Display Pi** to one replacement **Controller laptop**
- Each **Display Pi** has exactly one physical **Recovery PIN**
- **Recovery PIN** attempts are throttled by the **Display Pi**
- **Remote support** does not make the builder's laptop a second permanent **Controller laptop**
- **Remote support** requires a store employee to approve each temporary session
- Every Wi-Fi **Control request** is signed and replay-resistant
- Display images and read-only display traffic are not encrypted

## Example dialogue

> **Dev:** "Can another staff computer update a **Display Pi**?"
> **Domain expert:** "Not for now; only the **Controller laptop** is trusted."
>
> **Dev:** "Does the builder's LAN connection claim the Pi?"
> **Domain expert:** "Only temporarily; **Prepare for delivery** removes that trust before the client performs **Store onboarding**."
>
> **Dev:** "What happens if the store laptop is replaced?"
> **Domain expert:** "The client performs **Ownership recovery** over USB, and the old laptop loses access."
>
> **Dev:** "Is plugging in the USB cable enough to take ownership?"
> **Domain expert:** "No; the client must also enter that Pi's **Recovery PIN**."
>
> **Dev:** "Does helping the store remotely give the builder permanent control?"
> **Domain expert:** "No; **Remote support** is separate from ownership."

## Flagged ambiguities

- Multi-laptop control is deferred; the current trust model permits exactly one
  **Controller laptop** per **Display Pi**.
- "Initial setup" previously covered both preparation and client installation;
  resolved as **Builder setup** followed by **Store onboarding**.
- **Remote support** is attended; unattended access and custom remote infrastructure are out of scope.
- There are no legacy production units to migrate; secure onboarding is mandatory
  starting with the first client delivery.
